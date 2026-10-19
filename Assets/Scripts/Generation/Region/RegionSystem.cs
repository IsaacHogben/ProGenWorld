using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Runs the region generator's 2D jobs: per chunk column data (handed on as BiomeData, in place of
/// BiomeSystem's hints) and per chunk column height bands (which chunks of a column hold any
/// terrain, so only those are generated). Requests wait until the rivers around them are planned.
/// </summary>
public sealed class RegionSystem : IDisposable
{
    readonly int chunkSize;
    readonly int waterLevel;
    readonly float bankWidth;
    readonly BiomeDataManager biomeDataManager;
    RegionParams rp;
    NativeArray<BiomeShapeData> shapes;
    NativeArray<SubBiomeData> subBiomes;
    NativeArray<DetailSourceData> detailSources;
    readonly RiverNetwork rivers;

    public RegionParams Params => rp;
    public NativeArray<BiomeShapeData> Shapes => shapes;
    public NativeArray<SubBiomeData> SubBiomes => subBiomes;
    public NativeArray<DetailSourceData> DetailSources => detailSources;
    public RiverNetwork Rivers => rivers;

    // Column jobs scheduled per frame (chunks and column bands together)
    public int maxRequestsPerFrame = 16;
    int requestsThisFrame;

    struct Pending
    {
        public JobHandle handle;
        public NativeArray<ColumnSample> columns;
        public NativeArray<BiomeHint> hints;
    }

    readonly Dictionary<int3, Pending> pendingChunks = new();
    readonly Dictionary<int2, Pending> pendingColumns = new();
    readonly List<int3> waitingChunks = new();       // throttled, or rivers not planned yet
    readonly HashSet<int3> waitingChunkSet = new();
    readonly List<int2> waitingColumns = new();
    readonly HashSet<int2> waitingColumnSet = new();
    readonly List<int3> tmpChunkKeys = new();
    readonly List<int2> tmpColumnKeys = new();

    /// <summary>Column data for a chunk is ready (owned by the receiver: dispose with BiomeData.Dispose).</summary>
    public event Action<int3, BiomeData> OnChunkReady;

    /// <summary>
    /// A chunk column's terrain can span world y [min, max] (including water and how far 3D shapes
    /// reach); its surface and water lie within [surfaceMin, surfaceMax].
    /// Arguments: column, min, max, surfaceMin, surfaceMax.
    /// </summary>
    public event Action<int2, float, float, float, float> OnColumnBand;

    /// <param name="frequency">ChunkManager.frequency (FastNoise trees' world frequency, for calibrating 3D shapes).</param>
    public RegionSystem(RegionGenSettingsSO settings, BiomeDataManager biomeDataManager, int seed, int chunkSize, float frequency)
    {
        this.chunkSize = chunkSize;
        this.biomeDataManager = biomeDataManager;
        waterLevel = biomeDataManager.GetWaterLevel();
        bankWidth = settings.riverBankWidth;

        var defs = biomeDataManager.GetBiomeDefinitions();
        shapes = new NativeArray<BiomeShapeData>(defs.Length, Allocator.Persistent);
        detailSources = new NativeArray<DetailSourceData>(256, Allocator.Persistent);

        // A 3D shape tree's strength and reach (measured, unless overridden)
        DetailSourceData Source(TerrainTypeDefinitionSO terrain, float strength, DetailCalibration.Placement p)
        {
            if (terrain == null || strength <= 0f)
                return default;
            return new DetailSourceData
            {
                hasDetail = 1, strength = strength, rangeUp = p.up, rangeDown = p.down,
                overrideGradient = (byte)(terrain.overrideGradientThresholds ? 1 : 0),
                gradient1 = terrain.gradientThreshold1,
                gradient2 = terrain.gradientThreshold2
            };
        }

        var layers = new List<SubBiomeData>();
        int fallback = -1;
        foreach (var biome in biomeDataManager.biomes)
        {
            if (biome == null)
                continue;
            int id = biome.BiomeID;
            var s = biome.regionShape ?? new RegionShape();
            float hillFrequency = 1f / Mathf.Max(8f, s.hillScale);

            int subStart = layers.Count;
            if (s.subBiomes != null)
                foreach (var layer in s.subBiomes)
                {
                    if (layer == null)
                        continue;
                    int n = layers.Count;
                    float invScale = 1f / Mathf.Max(8f, layer.scale);
                    bool continuous = layer.kind == SubBiomeKind.Continuous;
                    layers.Add(new SubBiomeData
                    {
                        kind = (byte)layer.kind,
                        seed = (uint)(seed * 31 + 9001 + n * 7919),
                        invScale = invScale,
                        // fBm noise mostly sits within about +-0.5: map coverage onto that range
                        threshold = 0.5f * (1f - 2f * layer.coverage),
                        coverage = layer.coverage,
                        softness = Mathf.Max(0.01f, layer.softness),
                        noise = continuous
                            ? FbmSimplexNoise2D.Create(seed + 8001 + n, invScale, 3)
                            : FbmSimplexNoise2D.Create(seed + 8001 + n, invScale * 3f, 2),
                        heightOffset = layer.heightOffset,
                        hillMultiplier = layer.hillMultiplier,
                        detailMultiplier = layer.detailMultiplier,
                        decorationMultiplier = layer.decorationMultiplier,
                        surfaceBlock = (byte)layer.surfaceBlock,
                        detailSource = RegionDetailSources.LayerSource(defs.Length, layer, n),
                        hasDecorations = (byte)(layer.HasDecorations ? 1 : 0)
                    });
                    byte source = RegionDetailSources.LayerSource(defs.Length, layer, n);
                    if (source != RegionDetailSources.None)
                        detailSources[source] = Source(layer.detailTerrain, layer.detailStrength, layer.ResolveDetail(frequency, seed));
                    else if (layer.detailTerrain != null && defs.Length + n >= RegionDetailSources.None)
                        Debug.LogWarning($"[RegionGen] Too many sub-biome layers: the 3D shape of '{layer.name}' in {biome.biomeType} is ignored.");
                }

            var detail = s.detailTerrain != null && s.detailStrength > 0f
                ? Source(s.detailTerrain, s.detailStrength, s.ResolveDetail(frequency, seed))
                : default;
            detailSources[id] = detail;

            shapes[id] = new BiomeShapeData
            {
                defined = 1,
                baseHeight = s.baseHeight,
                hillHeight = s.hillHeight,
                ridged = s.ridged,
                terraceStep = s.terraceStep,
                terraceExponent = 1f + s.terraceSharpness * 15f,
                hillNoise = FbmSimplexNoise2D.Create(seed + 7001, hillFrequency, Mathf.Clamp(s.hillOctaves, 1, 8)),
                ridgeNoise = FbmSimplexNoise2D.Create(seed + 7002, hillFrequency, Mathf.Clamp(s.hillOctaves, 1, 8)),
                hasDetail = detail.hasDetail,
                detailStrength = s.detailStrength,
                detailRangeUp = detail.rangeUp,
                detailRangeDown = detail.rangeDown,
                rainfall = s.rainfall,
                sedimentBlock = (byte)biome.sedimentBlock,
                gradient1 = biome.gradientThreshold1,
                gradient2 = biome.gradientThreshold2,
                subStart = subStart,
                subCount = layers.Count - subStart
            };
            if (fallback < 0)
                fallback = id;
        }
        subBiomes = new NativeArray<SubBiomeData>(math.max(1, layers.Count), Allocator.Persistent);
        for (int i = 0; i < layers.Count; i++)
            subBiomes[i] = layers[i];

        var grid = biomeDataManager.GetClimateGrid();
        int defaultBiome = grid != null ? (int)grid.defaultBiome : -1;
        if (defaultBiome >= 0 && defaultBiome < shapes.Length && shapes[defaultBiome].defined != 0)
            fallback = defaultBiome;

        float size = settings.regionSize;
        float warpFrequency = 1f / (size * settings.borderWarpScale);
        float climateFrequency = 1f / settings.climateScale;
        rp = new RegionParams
        {
            seed = (uint)seed,
            regionSize = size,
            jitter = settings.regionJitter,
            warpAmplitude = settings.borderWarp * size,
            invBlend = 1f / Mathf.Max(1f, settings.borderBlend),
            warpX = FbmSimplexNoise2D.Create(seed + 7101, warpFrequency, 4),
            warpZ = FbmSimplexNoise2D.Create(seed + 7102, warpFrequency, 4),
            temperature = FbmSimplexNoise2D.Create(seed + 7201, climateFrequency, 3),
            humidity = FbmSimplexNoise2D.Create(seed + 7202, climateFrequency, 3),
            climateContrast = settings.climateContrast,
            humidityDivisions = grid != null ? grid.humidityDivisions : 1,
            temperatureDivisions = grid != null ? grid.temperatureDivisions : 1,
            fallbackBiome = (byte)math.max(0, fallback),
            macroBase = settings.macroBase,
            macroAmplitude = settings.macroAmplitude,
            macro = FbmSimplexNoise2D.Create(seed + 7301, 1f / settings.macroScale, 2),
            macroDetail = FbmSimplexNoise2D.Create(seed + 7302, 4f / settings.macroScale, 3),
            macroDetailAmplitude = settings.macroAmplitude * 0.3f,
            paletteEdge = settings.paletteEdgeNoise,
            minShapeWeight = settings.minShapeWeight,
            paletteNoise = FbmSimplexNoise2D.Create(seed + 7401, 1f / settings.paletteEdgeScale, 2)
        };

        rivers = new RiverNetwork(settings, this, biomeDataManager, seed);
    }

    static void Area(float2 origin, float spacing, int side, out float2 min, out float2 max)
    {
        min = origin - spacing;
        max = origin + side * spacing;
    }

    /// <summary>True when the rivers around a side x side grid at origin are planned (starts planning them).</summary>
    public bool IsAreaReady(float2 origin, float spacing, int side)
    {
        Area(origin, spacing, side, out var min, out var max);
        return rivers.EnsureArea(min, max);
    }

    /// <summary>Plans the rivers around a grid now (editor tools), so TryScheduleColumns succeeds.</summary>
    public void CompleteArea(float2 origin, float spacing, int side)
    {
        Area(origin, spacing, side, out var min, out var max);
        rivers.CompleteArea(min, max);
    }

    /// <summary>
    /// Schedules column data for a side x side grid of points spaced spacing apart from origin
    /// (x, z world). columns and hints hold side^2 entries and must live until the job is done.
    /// False (nothing scheduled) while the rivers around the grid are still being planned.
    /// </summary>
    public bool TryScheduleColumns(float2 origin, float spacing, int side,
        NativeArray<ColumnSample> columns, NativeArray<BiomeHint> hints, out JobHandle handle)
    {
        handle = default;
        Area(origin, spacing, side, out var min, out var max);
        if (!rivers.EnsureArea(min, max))
            return false;

        int rawSide = side + 2;
        int rawCount = rawSide * rawSide;
        var raw = new NativeArray<RawColumn>(rawCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var q = rivers.Query(min, max, Allocator.Persistent);

        var rawHandle = new RegionRawJob
        {
            origin = origin,
            spacing = spacing,
            rawSide = rawSide,
            rp = rp,
            waterLevel = waterLevel,
            bankWidth = bankWidth,
            shapes = shapes,
            subs = subBiomes,
            climateCells = biomeDataManager.GetClimateGridCells(),
            cellBiomes = biomeDataManager.GetClimateCellBiomes(),
            rarity = biomeDataManager.GetBiomeRarityWeights(),
            riverOrigin = q.origin,
            riverBucketSize = q.bucketSize,
            riverBuckets = q.buckets,
            riverEdges = q.edges,
            riverLakes = q.lakes,
            riverBucketRanges = q.bucketRanges,
            riverItems = q.items,
            output = raw
        }.Schedule(rawCount, 64);

        var columnHandle = new RegionColumnJob
        {
            origin = origin,
            spacing = spacing,
            side = side,
            rp = rp,
            shapes = shapes,
            sources = detailSources,
            subs = subBiomes,
            raw = raw,
            columns = columns,
            hints = hints
        }.Schedule(side * side, 64, rawHandle);

        handle = JobHandle.CombineDependencies(raw.Dispose(columnHandle), q.Dispose(rawHandle));
        return true;
    }

    bool TryScheduleChunkColumn(int2 column, out Pending pending)
    {
        pending = default;
        int side = chunkSize + 1;
        float2 origin = (float2)(column * chunkSize);
        if (!IsAreaReady(origin, 1f, side))
            return false;
        var columns = new NativeArray<ColumnSample>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var hints = new NativeArray<BiomeHint>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        TryScheduleColumns(origin, 1f, side, columns, hints, out var handle);
        requestsThisFrame++;
        pending = new Pending { handle = handle, columns = columns, hints = hints };
        return true;
    }

    /// <summary>Requests the column data for a chunk; OnChunkReady follows.</summary>
    public void RequestChunk(int3 coord)
    {
        if (pendingChunks.ContainsKey(coord) || waitingChunkSet.Contains(coord))
            return;
        if (requestsThisFrame < maxRequestsPerFrame && TryScheduleChunkColumn(coord.xz, out var p))
            pendingChunks.Add(coord, p);
        else if (waitingChunkSet.Add(coord))
            waitingChunks.Add(coord);
    }

    public void CancelChunk(int3 coord)
    {
        if (waitingChunkSet.Remove(coord))
            waitingChunks.Remove(coord);
        if (!pendingChunks.Remove(coord, out var p))
            return;
        p.handle.Complete();
        p.columns.Dispose();
        p.hints.Dispose();
    }

    /// <summary>Requests a chunk column's height band; OnColumnBand follows. False when throttled this frame.</summary>
    public bool RequestColumnBand(int2 column)
    {
        if (pendingColumns.ContainsKey(column) || waitingColumnSet.Contains(column))
            return true;
        if (requestsThisFrame >= maxRequestsPerFrame)
            return false;
        if (TryScheduleChunkColumn(column, out var p))
            pendingColumns.Add(column, p);
        else if (waitingColumnSet.Add(column))
            waitingColumns.Add(column);
        return true;
    }

    public bool IsColumnBandPending(int2 column) => pendingColumns.ContainsKey(column) || waitingColumnSet.Contains(column);

    public void Update()
    {
        requestsThisFrame = 0;
        rivers.Update();

        // Requests that were throttled or waiting for rivers, oldest first
        for (int i = 0; i < waitingColumns.Count && requestsThisFrame < maxRequestsPerFrame; i++)
        {
            var column = waitingColumns[i];
            if (!TryScheduleChunkColumn(column, out var p))
                continue;
            pendingColumns.Add(column, p);
            waitingColumnSet.Remove(column);
            waitingColumns.RemoveAt(i--);
        }
        for (int i = 0; i < waitingChunks.Count && requestsThisFrame < maxRequestsPerFrame; i++)
        {
            var coord = waitingChunks[i];
            if (!TryScheduleChunkColumn(coord.xz, out var p))
                continue;
            pendingChunks.Add(coord, p);
            waitingChunkSet.Remove(coord);
            waitingChunks.RemoveAt(i--);
        }

        if (pendingChunks.Count > 0)
        {
            tmpChunkKeys.Clear();
            tmpChunkKeys.AddRange(pendingChunks.Keys);
            foreach (var coord in tmpChunkKeys)
            {
                var p = pendingChunks[coord];
                if (!p.handle.IsCompleted)
                    continue;
                p.handle.Complete();
                pendingChunks.Remove(coord);
                OnChunkReady?.Invoke(coord, new BiomeData { resolution = chunkSize, grid = p.hints, columns = p.columns });
            }
        }

        if (pendingColumns.Count > 0)
        {
            tmpColumnKeys.Clear();
            tmpColumnKeys.AddRange(pendingColumns.Keys);
            foreach (var column in tmpColumnKeys)
            {
                var p = pendingColumns[column];
                if (!p.handle.IsCompleted)
                    continue;
                p.handle.Complete();
                pendingColumns.Remove(column);

                float lo = float.MaxValue, hi = float.MinValue;
                float surfaceLo = float.MaxValue, surfaceHi = float.MinValue;
                for (int i = 0; i < p.columns.Length; i++)
                {
                    var c = p.columns[i];
                    lo = math.min(lo, math.min(c.bandMin, c.height));
                    hi = math.max(hi, math.max(c.bandMax, c.waterY));
                    surfaceLo = math.min(surfaceLo, c.height);
                    surfaceHi = math.max(surfaceHi, math.max(c.height, c.waterY));
                }
                p.columns.Dispose();
                p.hints.Dispose();
                OnColumnBand?.Invoke(column, lo, hi, surfaceLo, surfaceHi);
            }
        }
    }

    public void Dispose()
    {
        foreach (var p in pendingChunks.Values)
        {
            p.handle.Complete();
            p.columns.Dispose();
            p.hints.Dispose();
        }
        foreach (var p in pendingColumns.Values)
        {
            p.handle.Complete();
            p.columns.Dispose();
            p.hints.Dispose();
        }
        pendingChunks.Clear();
        pendingColumns.Clear();
        rivers.Dispose();
        if (shapes.IsCreated)
            shapes.Dispose();
        if (subBiomes.IsCreated)
            subBiomes.Dispose();
        if (detailSources.IsCreated)
            detailSources.Dispose();
    }
}
