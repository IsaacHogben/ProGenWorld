using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class NoiseSystem
{
    public struct NoiseConfig
    {
        public int seed;
        public int chunkSize;
        public float frequency;
        public int maxConcurrentNoiseTasks;
        public BiomeDataManager biomeDataManager;
    }

    private NoiseConfig config;
    private NoiseGenerator noiseGenerator;
    private SemaphoreSlim limiter;
    private CancellationTokenSource cts;

    private Func<LODLevel, NativeArray<float>> rentDensity;
    private Action<LODLevel, NativeArray<float>> returnDensity;

    private readonly HashSet<int3> activeNoiseTasks = new HashSet<int3>();
    public int ActiveTasks => activeNoiseTasks.Count;

    public event Action<int3, LODLevel, NativeArray<float>> OnDensityReady;

    public void Initialize(
        NoiseConfig cfg,
        Func<LODLevel, NativeArray<float>> rentDensity,
        Action<LODLevel, NativeArray<float>> returnDensity)
    {
        config = cfg;
        this.rentDensity = rentDensity;
        this.returnDensity = returnDensity;

        noiseGenerator = new NoiseGenerator(cfg.seed);
        cts = new CancellationTokenSource();

        int cap = Math.Max(1, cfg.maxConcurrentNoiseTasks);
        limiter = new SemaphoreSlim(cap, cap);
    }

    public void Shutdown()
    {
        cts.Cancel();
        limiter?.Dispose();
        cts.Dispose();
        activeNoiseTasks.Clear();
    }

    public void RequestDensity(int3 coord, LODLevel lod, BiomeData biome)
    {
        if (config.biomeDataManager == null || !config.biomeDataManager.IsInitialized)
        {
            Debug.LogError("BiomeDataManager not initialized!");
            return;
        }

        int sampleRes = lod switch
        {
            LODLevel.Near => 1,
            LODLevel.Mid => 1,
            _ => 1
        };

        NativeArray<float> density = rentDensity(lod);

        lock (activeNoiseTasks)
            activeNoiseTasks.Add(coord);

        // Capture curve data reference (thread-safe, NativeArray is blittable)
        NativeArray<float> curveData = config.biomeDataManager.GetCurveData();
        NativeArray<TerrainTypeData> terrainTypes = config.biomeDataManager.GetTerrainTypeData();

        Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                await limiter.WaitAsync(cts.Token);
                acquired = true;

                if (cts.IsCancellationRequested)
                    return;

                Profiler.StartNoise();

                density.CopyFrom(
                    noiseGenerator.FillDensity(
                        coord,
                        config.chunkSize,
                        config.frequency,
                        sampleRes,
                        biome,
                        curveData,
                        terrainTypes
                    ));

                Profiler.EndNoise();

                OnDensityReady?.Invoke(coord, lod, density);
            }
            catch (OperationCanceledException)
            {
                if (density.IsCreated)
                    returnDensity(lod, density);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NoiseSystem] Noise task failed for {coord}: {ex}");
                if (density.IsCreated)
                    returnDensity(lod, density);
            }
            finally
            {
                if (acquired)
                    limiter.Release();

                lock (activeNoiseTasks)
                    activeNoiseTasks.Remove(coord);
            }
        });
    }
}