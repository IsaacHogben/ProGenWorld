using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Far horizon beyond the loaded chunks: a coarse, blocky copy of the terrain surface in rings of
/// growing voxel size (ViewDistanceSettings > Far Horizon). Each ring is a square of tiles around
/// the player; a tile is one or more ordinary packed chunks (64^3 voxels) whose voxels are many
/// blocks wide, drawn by the voxel renderer like any chunk. The surface height of every voxel
/// column is found by sampling the same biome blended terrain noise the chunks use, top down.
/// No caves, overhangs or decorations. The renderer hides each ring where the terrain inside it
/// (loaded chunks, or the next smaller ring) already is.
/// </summary>
public sealed class HorizonSystem : IDisposable
{
    public struct Config
    {
        public int chunkSize;
        public int seed;
        public float frequency;
        public BiomeSystem biomeSystem;
        public BiomeDataManager biomeDataManager;
        public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
        public VoxelWorldRenderer renderer;
        public ViewDistanceSettings settings;
    }

    readonly Config cfg;
    readonly int res;    // voxels per tile axis (the chunk resolution)
    readonly int side;   // res + 1: block arrays and height grids include the next tile's first column

    readonly FastNoise[] noises = new FastNoise[256];
    readonly float[] frequencyById = new float[256];
    readonly int[] yOffsetById = new int[256];
    readonly BiomeDefinition[] biomes;
    readonly int waterLevel;
    readonly int maxBricks, maxCells;

    class Tile
    {
        public readonly List<int> layers = new();
    }

    readonly Dictionary<int3, Tile> tiles = new();        // (ring, tile x, tile z)
    readonly HashSet<int3> wanted = new();
    readonly List<(int3 key, float dist)> missing = new();
    readonly List<int3> tmpKeys = new();
    readonly Vector4[] innerSquares = new Vector4[VoxelGpuFormat.MaxHorizonRings];
    int settingsHash;

    // One tile in flight at a time
    enum Stage { Idle, Biome, Heights, Pack }
    Stage stage;
    int3 workKey;
    JobHandle biomeHandle;
    NativeArray<float> terrainGrid, humidityGrid, temperatureGrid;
    NativeArray<BiomeHint> hintGrid;
    Task<List<(int layer, byte[] blocks)>> heightTask;
    readonly List<(int layer, NativeArray<byte> blocks, VoxelPackResult result, JobHandle handle)> packs = new();

    public int TileCount => tiles.Count;
    public int PendingTiles => missing.Count;
    public bool Busy => stage != Stage.Idle;

    public HorizonSystem(Config config)
    {
        cfg = config;
        res = VoxelGpuFormat.Resolution(cfg.chunkSize);
        side = res + 1;
        maxBricks = VoxelGpuFormat.TotalBricks(cfg.chunkSize);
        maxCells = VoxelGpuFormat.MaxCells(cfg.chunkSize);

        foreach (var terrain in cfg.biomeDataManager.terrainTypes)
        {
            if (terrain == null || string.IsNullOrEmpty(terrain.noiseNodeTree))
                continue;
            try
            {
                byte id = terrain.TerrainID;
                noises[id] = FastNoise.FromEncodedNodeTree(terrain.noiseNodeTree);
                frequencyById[id] = cfg.frequency / 100f * terrain.frequencyMultiplier;
                yOffsetById[id] = terrain.yOffset;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Horizon] Noise for terrain '{terrain.terrainTypeName}' failed: {e.Message}");
            }
        }

        biomes = cfg.biomeDataManager.GetBiomeDefinitions().ToArray();
        waterLevel = cfg.biomeDataManager.GetWaterLevel();
    }

    float VoxelSize(int ring) => (float)(cfg.settings.horizonVoxelSize << ring);
    int TileSize(int ring) => res * (cfg.settings.horizonVoxelSize << ring);

    /// <summary>
    /// Updates which tiles each ring needs around the player and advances the tile being built.
    /// loadedCenter / loadedRadius: the area the loaded chunks are known to cover (world units).
    /// minY / maxY: world heights searched for the terrain surface.
    /// </summary>
    public void Update(Vector3 playerPos, Vector2 loadedCenter, float loadedRadius, float minY, float maxY)
    {
        var st = cfg.settings;
        int hash = HashCode.Combine(st.farHorizon, st.horizonRings, st.horizonVoxelSize, st.horizonRingRadius);
        if (hash != settingsHash)
        {
            settingsHash = hash;
            Clear();
        }
        if (!st.farHorizon || st.horizonVoxelSize < 1)
        {
            cfg.renderer.SetHorizonHoles(loadedCenter, loadedRadius, null);
            return;
        }

        int rings = math.clamp(st.horizonRings, 1, VoxelGpuFormat.MaxHorizonRings);
        float2 p = new float2(playerPos.x, playerPos.z);
        float2 center = loadedCenter;

        wanted.Clear();
        missing.Clear();
        float coveredHalf = loadedRadius;   // half width of the square the rings inside already cover
        for (int ring = 0; ring < rings; ring++)
        {
            int size = TileSize(ring);
            int2 c = (int2)math.floor(p / size);
            // Each ring must reach past the one inside it (or the loaded area).
            int k = math.max(st.horizonRingRadius, (int)math.ceil(coveredHalf / size) + 1);

            float2 lo = (float2)(c - k) * size;
            float2 hi = (float2)(c + k + 1) * size;
            if (ring > 0)
                innerSquares[ring] = PrevSquare;
            PrevSquare = new Vector4(lo.x, lo.y, hi.x, hi.y);

            for (int tz = c.y - k; tz <= c.y + k; tz++)
            for (int tx = c.x - k; tx <= c.x + k; tx++)
            {
                float2 tMin = new float2(tx, tz) * size;
                float2 tMax = tMin + size;

                // Entirely inside the loaded chunks, or inside the ring within this one
                float2 far = math.max(math.abs(tMin - center), math.abs(tMax - center));
                if (math.length(far) < loadedRadius)
                    continue;
                if (ring > 0)
                {
                    var sq = innerSquares[ring];
                    if (tMin.x >= sq.x && tMin.y >= sq.y && tMax.x <= sq.z && tMax.y <= sq.w)
                        continue;
                }

                var key = new int3(ring, tx, tz);
                wanted.Add(key);
                if (!tiles.ContainsKey(key) && !(stage != Stage.Idle && workKey.Equals(key)))
                    missing.Add((key, ring * 1e7f + math.distance(p, (tMin + tMax) * 0.5f)));
            }
            coveredHalf = math.min(math.min(p.x - lo.x, p.y - lo.y), math.min(hi.x - p.x, hi.y - p.y));
        }
        for (int i = rings; i < innerSquares.Length; i++)
            innerSquares[i] = Vector4.zero;
        cfg.renderer.SetHorizonHoles(loadedCenter, loadedRadius, innerSquares);

        // Drop tiles the rings no longer cover
        tmpKeys.Clear();
        foreach (var key in tiles.Keys)
            if (!wanted.Contains(key))
                tmpKeys.Add(key);
        foreach (var key in tmpKeys)
            RemoveTile(key);

        Pump(minY, maxY);
    }

    Vector4 PrevSquare;

    void Pump(float minY, float maxY)
    {
        switch (stage)
        {
            case Stage.Idle:
            {
                if (missing.Count == 0)
                    return;
                int best = 0;
                for (int i = 1; i < missing.Count; i++)
                    if (missing[i].dist < missing[best].dist)
                        best = i;
                workKey = missing[best].key;
                StartBiome(workKey);
                stage = Stage.Biome;
                return;
            }

            case Stage.Biome:
            {
                if (!biomeHandle.IsCompleted)
                    return;
                biomeHandle.Complete();
                var hints = hintGrid.ToArray();
                DisposeBiomeArrays();

                int ring = workKey.x;
                int2 tile = workKey.yz;
                float lo = minY, hi = maxY;
                heightTask = Task.Run(() => BuildLayers(ring, tile, hints, lo, hi));
                stage = Stage.Heights;
                return;
            }

            case Stage.Heights:
            {
                if (!heightTask.IsCompleted)
                    return;
                if (heightTask.IsFaulted)
                {
                    Debug.LogError($"[Horizon] Tile {workKey} failed: {heightTask.Exception?.GetBaseException()}");
                    heightTask = null;
                    stage = Stage.Idle;
                    return;
                }

                foreach (var (layer, data) in heightTask.Result)
                {
                    var blocks = new NativeArray<byte>(data, Allocator.Persistent);
                    var result = VoxelPackResult.Create(maxBricks, maxCells);
                    var job = new VoxelPackJob
                    {
                        blocks = blocks,
                        blockDb = cfg.blockDb,
                        chunkSize = cfg.chunkSize,
                        coord = new int3(workKey.y, layer, workKey.z),
                        collectECSSpawns = false,
                        minLevel = 0,
                        inputLevel = 0,
                        bricks = result.bricks,
                        pages = result.pages,
                        meta = result.meta,
                        cells = result.cells,
                        ecsSpawns = result.ecsSpawns
                    };
                    packs.Add((layer, blocks, result, job.Schedule()));
                }
                heightTask = null;
                stage = Stage.Pack;
                return;
            }

            case Stage.Pack:
            {
                foreach (var p in packs)
                    if (!p.handle.IsCompleted)
                        return;

                bool keep = wanted.Contains(workKey);
                var tile = new Tile();
                int ring = workKey.x;
                int size = TileSize(ring);
                foreach (var (layer, blocks, result, handle) in packs)
                {
                    handle.Complete();
                    if (keep && !result.IsEmpty)
                    {
                        var origin = new float3(workKey.y * (float)size, layer * (float)size, workKey.z * (float)size);
                        cfg.renderer.SetHorizonChunk(ring, new int3(workKey.y, layer, workKey.z), origin, VoxelSize(ring), result);
                        tile.layers.Add(layer);
                    }
                    blocks.Dispose();
                    result.Dispose();
                }
                packs.Clear();
                if (keep)
                    tiles[workKey] = tile;
                stage = Stage.Idle;
                return;
            }
        }
    }

    void StartBiome(int3 key)
    {
        int count = side * side;
        terrainGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        humidityGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        temperatureGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        hintGrid = new NativeArray<BiomeHint>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        biomeHandle = cfg.biomeSystem.ScheduleHints(new int3(key.y, 0, key.z), TileSize(key.x), res,
            terrainGrid, humidityGrid, temperatureGrid, hintGrid);
    }

    void DisposeBiomeArrays()
    {
        if (terrainGrid.IsCreated) terrainGrid.Dispose();
        if (humidityGrid.IsCreated) humidityGrid.Dispose();
        if (temperatureGrid.IsCreated) temperatureGrid.Dispose();
        if (hintGrid.IsCreated) hintGrid.Dispose();
    }

    void RemoveTile(int3 key)
    {
        if (!tiles.Remove(key, out var tile))
            return;
        foreach (int layer in tile.layers)
            cfg.renderer.RemoveHorizonChunk(key.x, new int3(key.y, layer, key.z));
    }

    /// <summary>Removes every tile (settings changed or the horizon was switched off).</summary>
    public void Clear()
    {
        tmpKeys.Clear();
        tmpKeys.AddRange(tiles.Keys);
        foreach (var key in tmpKeys)
            RemoveTile(key);
        CancelWork();
    }

    void CancelWork()
    {
        biomeHandle.Complete();
        DisposeBiomeArrays();
        foreach (var p in packs)
        {
            p.handle.Complete();
            p.blocks.Dispose();
            p.result.Dispose();
        }
        packs.Clear();
        heightTask = null;   // a running height task just finishes unobserved
        stage = Stage.Idle;
    }

    public void Dispose() => CancelWork();

    // ------------------------------------------------------------------------------------
    // Worker thread: surface heights and block arrays
    // ------------------------------------------------------------------------------------

    // Surface height (top of the highest solid voxel) of every column of a tile, then one block
    // array per vertical layer (res voxels tall) that holds terrain. Columns are solid from the
    // surface down to a little below the tile's lowest surface; deeper voxels are left as air
    // since they are never seen.
    List<(int layer, byte[] blocks)> BuildLayers(int ring, int2 tile, BiomeHint[] hints, float minY, float maxY)
    {
        float v = VoxelSize(ring);
        float size = TileSize(ring);
        float ox = tile.x * size, oz = tile.y * size;
        int columns = side * side;

        // Top down search: coarse steps of 4 voxels, then the voxels between the last open and
        // the first solid coarse sample. Samples sit at voxel centres.
        float top = (math.floor(maxY / v) + 0.5f) * v;
        const int fineSteps = 4;
        float coarse = v * fineSteps;
        int coarseCount = math.max(1, (int)math.ceil((top - minY) / coarse) + 1);

        var coarseY = new float[coarseCount];
        for (int k = 0; k < coarseCount; k++)
            coarseY[k] = top - k * coarse;

        var heights = new float[columns];
        var firstSolid = new int[columns];
        var density = SampleColumns(hints, ox, oz, v, columns, coarseCount, (col, k) => coarseY[k]);
        for (int col = 0; col < columns; col++)
        {
            firstSolid[col] = -1;
            for (int k = 0; k < coarseCount; k++)
            {
                if (density[col * coarseCount + k] <= 0f)
                {
                    firstSolid[col] = k;
                    break;
                }
            }
        }

        // Refine between the solid coarse sample and the open one above it
        var fine = SampleColumns(hints, ox, oz, v, columns, fineSteps - 1,
            (col, j) => firstSolid[col] > 0 ? coarseY[firstSolid[col]] + (fineSteps - 1 - j) * v : float.NaN);
        float minH = float.MaxValue, maxH = float.MinValue;
        for (int col = 0; col < columns; col++)
        {
            int k = firstSolid[col];
            float centre;
            if (k < 0)
                centre = minY - v;                 // nothing solid in the searched range
            else if (k == 0)
                centre = coarseY[0];               // solid at the top of the range
            else
            {
                centre = coarseY[k];
                for (int j = 0; j < fineSteps - 1; j++)
                {
                    if (fine[col * (fineSteps - 1) + j] <= 0f)
                    {
                        centre = coarseY[k] + (fineSteps - 1 - j) * v;
                        break;
                    }
                }
            }
            float h = centre + v * 0.5f;
            heights[col] = h;
            minH = math.min(minH, h);
            maxH = math.max(maxH, math.max(h, waterLevel + 1f));
        }

        // Block arrays
        var layersOut = new List<(int, byte[])>();
        float fillBottom = minH - 2f * v;
        int l0 = (int)math.floor(fillBottom / size);
        int l1 = (int)math.floor((maxH - 0.001f) / size);
        byte water = (byte)BlockType.Water;

        for (int layer = l0; layer <= l1; layer++)
        {
            var blocks = new byte[side * side * side];
            float baseY = layer * size;
            bool any = false;
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                int col = x + z * side;
                float h = heights[col];
                var hint = hints[col];
                var biome = hint.primaryBiome < biomes.Length ? biomes[hint.primaryBiome] : default;
                bool underwater = h <= waterLevel + 1f;

                for (int y = 0; y < side; y++)
                {
                    float wy = baseY + (y + 0.5f) * v;
                    byte id = 0;
                    if (wy >= fillBottom)
                    {
                        if (wy < h)
                            id = wy >= h - v ? (underwater ? biome.sedimentBlock : biome.surfaceBlock) : biome.deepBlock;
                        else if (wy < waterLevel + 1f)
                            id = water;
                    }
                    if (id != 0)
                    {
                        blocks[x + y * side + z * side * side] = id;
                        any = true;
                    }
                }
            }
            if (any)
                layersOut.Add((layer, blocks));
        }
        return layersOut;
    }

    // Terrain density at count points per column (yAt(column, i) gives each point's height; NaN
    // skips it). Same blend as NoiseGenerator.BlendTerrainNoises: primary terrain noise, lerped to
    // the secondary one by the biome hint's terrain blend.
    float[] SampleColumns(BiomeHint[] hints, float ox, float oz, float v, int columns, int count, Func<int, int, float> yAt)
    {
        var result = new float[columns * count];
        var primary = new float[columns * count];
        var secondary = new float[columns * count];

        Span<bool> used = stackalloc bool[256];
        for (int col = 0; col < columns; col++)
        {
            used[hints[col].primaryTerrainType] = true;
            if (hints[col].terrainBlend > 0)
                used[hints[col].secondaryTerrainType] = true;
        }

        var xs = new List<float>(columns * count);
        var ys = new List<float>(columns * count);
        var zs = new List<float>(columns * count);
        var targets = new List<int>(columns * count);   // index * 2 + (0 primary, 1 secondary)

        for (int id = 0; id < 256; id++)
        {
            if (!used[id] || noises[id] == null)
                continue;

            float f = frequencyById[id];
            int yOffset = yOffsetById[id];
            xs.Clear(); ys.Clear(); zs.Clear(); targets.Clear();

            for (int col = 0; col < columns; col++)
            {
                var hint = hints[col];
                bool isPrimary = hint.primaryTerrainType == id;
                bool isSecondary = hint.terrainBlend > 0 && hint.secondaryTerrainType == id;
                if (!isPrimary && !isSecondary)
                    continue;

                int x = col % side, z = col / side;
                float wx = ox + (x + 0.5f) * v;
                float wz = oz + (z + 0.5f) * v;
                for (int i = 0; i < count; i++)
                {
                    float wy = yAt(col, i);
                    if (float.IsNaN(wy))
                        continue;
                    int index = col * count + i;
                    if (isPrimary) { xs.Add(wx * f); ys.Add((wy + yOffset) * f); zs.Add(wz * f); targets.Add(index * 2); }
                    if (isSecondary) { xs.Add(wx * f); ys.Add((wy + yOffset) * f); zs.Add(wz * f); targets.Add(index * 2 + 1); }
                }
            }
            if (targets.Count == 0)
                continue;

            var output = new float[targets.Count];
            noises[id].GenPositionArray3D(output, xs.ToArray(), ys.ToArray(), zs.ToArray(), 0f, 0f, 0f, cfg.seed);
            for (int i = 0; i < targets.Count; i++)
            {
                int t = targets[i];
                if ((t & 1) == 0) primary[t >> 1] = output[i];
                else secondary[t >> 1] = output[i];
            }
        }

        for (int col = 0; col < columns; col++)
        {
            var hint = hints[col];
            float blend = hint.terrainBlend * (1f / 255f);
            for (int i = 0; i < count; i++)
            {
                int index = col * count + i;
                float pv = primary[index];
                result[index] = blend > 0.001f ? pv + (secondary[index] - pv) * blend : pv;
            }
        }
        return result;
    }
}
