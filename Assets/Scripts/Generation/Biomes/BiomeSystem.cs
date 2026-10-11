using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections.Generic;

public sealed class BiomeSystem : MonoBehaviour
{
    [Header("References")]
    public BiomeDataManager biomeDataManager;

    [Header("Biome Settings")]
    public int biomeResolution = 64;

    [Header("Noise Frequencies")]
    [Tooltip("Base frequency for all noise")]
    public float baseFrequency = 0.001f;

    [Tooltip("Terrain frequency multiplier")]
    [Range(0.1f, 5f)]
    public float terrainFrequencyMultiplier = 4.0f;

    [Tooltip("Climate frequency multiplier")]
    [Range(0.1f, 5f)]
    public float climateFrequencyMultiplier = 0.7f;

    [Header("Transition Settings")]
    [Tooltip("Terrain blend width (0.1 = sharp, 1.0 = gradual)")]
    [Range(0.1f, 1.0f)]
    public float terrainBlendWidth = 0.3f;

    [Tooltip("Biome blend width (0.1 = sharp, 1.0 = gradual)")]
    [Range(0.1f, 2.0f)]
    public float biomeBlendWidth = 1.0f;
    int seed = 1337;

    // Noise settings, sampled in BiomeNoiseJob
    FbmSimplexNoise2D terrainNoise;
    FbmSimplexNoise2D humidityNoise;
    FbmSimplexNoise2D temperatureNoise;

    // Task throttleing
    [Header("Throttling")]
    [Tooltip("Biome jobs scheduled per frame. Each costs a few small allocations and two job schedules on the main thread.")]
    [Min(1)] public int maxRequestsPerFrame = 8;
    private int biomeRequestsThisFrame = 0;
    private Queue<(int3 coord, int chunkSize)> deferredBiomeRequests = new Queue<(int3, int)>();

    public void SetSeed(int value)
    {
        seed = value;
        InitializeNoiseGenerators();
    }

    void Awake()
    {
        InitializeNoiseGenerators();
    }

    void InitializeNoiseGenerators()
    {
        // OpenSimplex2, FBm, 3 octaves, lacunarity 2, gain 0.5

        // Terrain noise - higher frequency for more variation
        terrainNoise = FbmSimplexNoise2D.Create(seed, baseFrequency * terrainFrequencyMultiplier);

        // Humidity noise - lower frequency for broader climate zones
        humidityNoise = FbmSimplexNoise2D.Create(seed + 1000, baseFrequency * climateFrequencyMultiplier);

        // Temperature noise - lower frequency for broader climate zones
        temperatureNoise = FbmSimplexNoise2D.Create(seed + 5000, baseFrequency * climateFrequencyMultiplier);
    }

    struct PendingBiome
    {
        public JobHandle handle;
        public NativeArray<BiomeHint> grid;
        public NativeArray<float> terrainNoise;
        public NativeArray<float> humidityNoise;
        public NativeArray<float> temperatureNoise;
    }

    readonly Dictionary<int3, PendingBiome> pending = new();
    public System.Action<int3, BiomeData> OnBiomeCompleted;

    public void RequestBiome(int3 chunkCoord, int chunkSize)
    {
        if (pending.ContainsKey(chunkCoord))
            return;

        // Throttle: defer if we've hit the limit this frame
        if (biomeRequestsThisFrame >= maxRequestsPerFrame)
        {
            if (!deferredBiomeRequests.Contains((chunkCoord, chunkSize)))
                deferredBiomeRequests.Enqueue((chunkCoord, chunkSize));
            return;
        }

        RequestBiomeInternal(chunkCoord, chunkSize);
    }

    private void RequestBiomeInternal(int3 chunkCoord, int chunkSize)
    {
        UnityEngine.Profiling.Profiler.BeginSample("BiomeSystem: RequestBiomeInternal");
        biomeRequestsThisFrame++;
        if (pending.ContainsKey(chunkCoord))
            return;

        if (biomeDataManager == null || !biomeDataManager.IsInitialized)
        {
            Debug.LogError("BiomeDataManager not initialized!");
            return;
        }

        var climateGrid = biomeDataManager.GetClimateGrid();
        if (climateGrid == null)
        {
            Debug.LogError("No ClimateGrid assigned to BiomeDataManager!");
            return;
        }

        int side = biomeResolution + 1;
        int count = side * side;

        // Noise grids [0,1], sampled on worker threads before BiomeJob reads them
        var terrainNoiseGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var humidityNoiseGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var temperatureNoiseGrid = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        var noiseJob = new BiomeNoiseJob
        {
            chunkCoord = chunkCoord,
            chunkSize = chunkSize,
            resolution = biomeResolution,
            terrainNoise = terrainNoise,
            humidityNoise = humidityNoise,
            temperatureNoise = temperatureNoise,
            terrainNoiseGrid = terrainNoiseGrid,
            humidityNoiseGrid = humidityNoiseGrid,
            temperatureNoiseGrid = temperatureNoiseGrid
        };

        var grid = new NativeArray<BiomeHint>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        var job = new BiomeJob
        {
            chunkCoord = chunkCoord,
            chunkSize = chunkSize,
            resolution = biomeResolution,
            waterLevel = biomeDataManager.GetWaterLevel(),
            terrainTypes = biomeDataManager.GetTerrainTypeData(),
            terrainAllowedBiomes = biomeDataManager.GetTerrainAllowedBiomes(),
            biomes = biomeDataManager.GetBiomeDefinitions(),

            // Climate grid data
            humidityDivisions = climateGrid.humidityDivisions,
            temperatureDivisions = climateGrid.temperatureDivisions,
            defaultBiome = (byte)climateGrid.defaultBiome,
            climateGrid = biomeDataManager.GetClimateGridCells(),
            climateCellBiomes = biomeDataManager.GetClimateCellBiomes(),
            biomeRarityWeights = biomeDataManager.GetBiomeRarityWeights(),
            biomePriority = biomeDataManager.GetBiomePriority(),

            // Noise grids
            terrainNoiseGrid = terrainNoiseGrid,
            humidityNoiseGrid = humidityNoiseGrid,
            temperatureNoiseGrid = temperatureNoiseGrid,

            // Settings
            terrainBlendWidth = terrainBlendWidth,
            biomeBlendWidth = biomeBlendWidth,

            randomSeed = (uint)seed,

            output = grid
        };

        Profiler.StartBiome();
        JobHandle noiseHandle = noiseJob.Schedule(count, 64);
        JobHandle handle = job.Schedule(count, 64, noiseHandle);

        pending.Add(chunkCoord, new PendingBiome
        {
            handle = handle,
            grid = grid,
            terrainNoise = terrainNoiseGrid,
            humidityNoise = humidityNoiseGrid,
            temperatureNoise = temperatureNoiseGrid
        });
        UnityEngine.Profiling.Profiler.EndSample();
    }

    public void CancelBiome(int3 chunkCoord)
    {
        if (!pending.TryGetValue(chunkCoord, out var p))
            return;

        p.handle.Complete();
        if (p.grid.IsCreated) p.grid.Dispose();
        if (p.terrainNoise.IsCreated) p.terrainNoise.Dispose();
        if (p.humidityNoise.IsCreated) p.humidityNoise.Dispose();
        if (p.temperatureNoise.IsCreated) p.temperatureNoise.Dispose();

        pending.Remove(chunkCoord);
    }

    static readonly List<int3> tmpKeys = new List<int3>(128);

    public void Updater()
    {
        // Reset counter each frame
        biomeRequestsThisFrame = 0;

        // Process deferred requests from previous frames
        while (deferredBiomeRequests.Count > 0 && biomeRequestsThisFrame < maxRequestsPerFrame)
        {
            var (coord, size) = deferredBiomeRequests.Dequeue();
            RequestBiomeInternal(coord, size);
        }

        if (pending.Count == 0)
            return;

        tmpKeys.Clear();
        tmpKeys.AddRange(pending.Keys);

        for (int i = 0; i < tmpKeys.Count; i++)
        {
            int3 coord = tmpKeys[i];
            var p = pending[coord];

            if (!p.handle.IsCompleted)
                continue;

            p.handle.Complete();
            Profiler.EndBiome();

            var biomeData = new BiomeData
            {
                resolution = biomeResolution,
                grid = p.grid
            };

            // Dispose noise grids (we don't need them anymore)
            if (p.terrainNoise.IsCreated) p.terrainNoise.Dispose();
            if (p.humidityNoise.IsCreated) p.humidityNoise.Dispose();
            if (p.temperatureNoise.IsCreated) p.temperatureNoise.Dispose();

            pending.Remove(coord);
            OnBiomeCompleted?.Invoke(coord, biomeData);
        }
    }

    void OnDestroy()
    {
        foreach (var kv in pending)
        {
            kv.Value.handle.Complete();
            if (kv.Value.grid.IsCreated) kv.Value.grid.Dispose();
            if (kv.Value.terrainNoise.IsCreated) kv.Value.terrainNoise.Dispose();
            if (kv.Value.humidityNoise.IsCreated) kv.Value.humidityNoise.Dispose();
            if (kv.Value.temperatureNoise.IsCreated) kv.Value.temperatureNoise.Dispose();
        }
        pending.Clear();
    }
}