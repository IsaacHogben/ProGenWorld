using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Runs the region generator's 2D jobs: per chunk column data (handed on as BiomeData, in place of
/// BiomeSystem's hints) and per chunk column height bands (which chunks of a column hold any
/// terrain, so only those are generated).
/// </summary>
public sealed class RegionSystem : IDisposable
{
    readonly int chunkSize;
    readonly int waterLevel;
    readonly BiomeDataManager biomeDataManager;
    RegionParams rp;
    NativeArray<BiomeShapeData> shapes;

    public RegionParams Params => rp;
    public NativeArray<BiomeShapeData> Shapes => shapes;

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
    readonly Queue<int3> deferredChunks = new();
    readonly HashSet<int3> deferredChunkSet = new();
    readonly List<int3> tmpChunkKeys = new();
    readonly List<int2> tmpColumnKeys = new();

    /// <summary>Column data for a chunk is ready (owned by the receiver: dispose with BiomeData.Dispose).</summary>
    public event Action<int3, BiomeData> OnChunkReady;

    /// <summary>A chunk column's terrain spans world y [min, max] (including water and 3D shapes).</summary>
    public event Action<int2, float, float> OnColumnBand;

    public RegionSystem(RegionGenSettingsSO settings, BiomeDataManager biomeDataManager, int seed, int chunkSize)
    {
        this.chunkSize = chunkSize;
        this.biomeDataManager = biomeDataManager;
        waterLevel = biomeDataManager.GetWaterLevel();

        var defs = biomeDataManager.GetBiomeDefinitions();
        shapes = new NativeArray<BiomeShapeData>(defs.Length, Allocator.Persistent);

        int fallback = -1;
        foreach (var biome in biomeDataManager.biomes)
        {
            if (biome == null)
                continue;
            int id = biome.BiomeID;
            var s = biome.regionShape ?? new RegionShape();
            float hillFrequency = 1f / Mathf.Max(8f, s.hillScale);
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
                hasDetail = (byte)(s.detailTerrain != null && s.detailStrength > 0f ? 1 : 0),
                detailStrength = s.detailStrength,
                detailRangeUp = s.detailRangeUp,
                detailRangeDown = s.detailRangeDown
            };
            if (fallback < 0)
                fallback = id;
        }

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
            macro = FbmSimplexNoise2D.Create(seed + 7301, 1f / settings.macroScale, 4),
            paletteEdge = settings.paletteEdgeNoise,
            paletteNoise = FbmSimplexNoise2D.Create(seed + 7401, 1f / settings.paletteEdgeScale, 2)
        };
    }

    /// <summary>
    /// Schedules column data for a side x side grid of points spaced spacing apart from origin
    /// (x, z world). columns and hints hold side^2 entries and must live until the job is done.
    /// </summary>
    public JobHandle ScheduleColumns(float2 origin, float spacing, int side,
        NativeArray<ColumnSample> columns, NativeArray<BiomeHint> hints)
    {
        int rawSide = side + 2;
        int rawCount = rawSide * rawSide;
        var heights = new NativeArray<float>(rawCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var weights = new NativeArray<RegionWeights>(rawCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        var raw = new RegionRawJob
        {
            origin = origin,
            spacing = spacing,
            rawSide = rawSide,
            rp = rp,
            shapes = shapes,
            climateCells = biomeDataManager.GetClimateGridCells(),
            cellBiomes = biomeDataManager.GetClimateCellBiomes(),
            rarity = biomeDataManager.GetBiomeRarityWeights(),
            heights = heights,
            weights = weights
        }.Schedule(rawCount, 64);

        var handle = new RegionColumnJob
        {
            origin = origin,
            spacing = spacing,
            side = side,
            rp = rp,
            shapes = shapes,
            heights = heights,
            weights = weights,
            columns = columns,
            hints = hints
        }.Schedule(side * side, 64, raw);

        return JobHandle.CombineDependencies(heights.Dispose(handle), weights.Dispose(handle));
    }

    Pending ScheduleChunkColumn(int2 column)
    {
        int side = chunkSize + 1;
        var columns = new NativeArray<ColumnSample>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var hints = new NativeArray<BiomeHint>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var handle = ScheduleColumns((float2)(column * chunkSize), 1f, side, columns, hints);
        requestsThisFrame++;
        return new Pending { handle = handle, columns = columns, hints = hints };
    }

    /// <summary>Requests the column data for a chunk; OnChunkReady follows.</summary>
    public void RequestChunk(int3 coord)
    {
        if (pendingChunks.ContainsKey(coord))
            return;
        if (requestsThisFrame >= maxRequestsPerFrame)
        {
            if (deferredChunkSet.Add(coord))
                deferredChunks.Enqueue(coord);
            return;
        }
        pendingChunks.Add(coord, ScheduleChunkColumn(coord.xz));
    }

    public void CancelChunk(int3 coord)
    {
        deferredChunkSet.Remove(coord);
        if (!pendingChunks.Remove(coord, out var p))
            return;
        p.handle.Complete();
        p.columns.Dispose();
        p.hints.Dispose();
    }

    /// <summary>Requests a chunk column's height band; OnColumnBand follows. False when throttled this frame.</summary>
    public bool RequestColumnBand(int2 column)
    {
        if (pendingColumns.ContainsKey(column))
            return true;
        if (requestsThisFrame >= maxRequestsPerFrame)
            return false;
        pendingColumns.Add(column, ScheduleChunkColumn(column));
        return true;
    }

    public bool IsColumnBandPending(int2 column) => pendingColumns.ContainsKey(column);

    public void Update()
    {
        requestsThisFrame = 0;
        while (deferredChunks.Count > 0 && requestsThisFrame < maxRequestsPerFrame)
        {
            var coord = deferredChunks.Dequeue();
            if (deferredChunkSet.Remove(coord) && !pendingChunks.ContainsKey(coord))
                pendingChunks.Add(coord, ScheduleChunkColumn(coord.xz));
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
                for (int i = 0; i < p.columns.Length; i++)
                {
                    var c = p.columns[i];
                    lo = math.min(lo, math.min(c.bandMin, c.height));
                    hi = math.max(hi, c.bandMax);
                }
                // Water fills low ground up to the water level
                hi = math.max(hi, waterLevel + 1f);
                p.columns.Dispose();
                p.hints.Dispose();
                OnColumnBand?.Invoke(column, lo, hi);
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
        if (shapes.IsCreated)
            shapes.Dispose();
    }
}
