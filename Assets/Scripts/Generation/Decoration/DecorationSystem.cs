using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class DecorationSystem
{
    public struct DecorationConfig
    {
        public int chunkSize;
        public int indexSize;
        public int seed;
        public Func<bool> GetBootstrapMode;
        public Func<float> GetCompleteBudgetMs;   // main-thread time per frame for completing jobs (at least one completes)
        public BiomeDataManager biomeDataManager;
    }

    private DecorationConfig config;
    private readonly Dictionary<int3, JobHandle> jobHandles = new();
    private readonly Dictionary<int3, NativeList<PendingBlockWrite>> outputLists = new();
    private readonly Dictionary<int3, (NativeArray<byte> blockIds, LODLevel lod)> inputs = new();

    public int ActiveJobs => jobHandles.Count;

    public event Action<int3> OnDecorationStarted;
    public event Action<int3, NativeList<PendingBlockWrite>> OnDecorationCompleted;

    private static int3[] keyBuffer = new int3[256];
    private int keyCount = 0;

    public void Initialize(DecorationConfig config)
    {
        this.config = config;
    }

    public void ScheduleDecoration(int3 coord, LODLevel lod, NativeArray<byte> blockIds, BiomeData biomeData)
    {
        if (config.biomeDataManager == null || !config.biomeDataManager.IsInitialized)
        {
            Debug.LogError("BiomeDataManager not initialized!");
            return;
        }

        var writes = new NativeList<PendingBlockWrite>(Allocator.Persistent);

        var job = new DecorationJob
        {
            chunkCoord = coord,
            chunkSize = config.chunkSize,
            indexSize = config.indexSize,
            lod = lod,
            rng = new Unity.Mathematics.Random((uint)(config.seed ^ coord.GetHashCode())),
            blockIds = blockIds,
            pendingWrites = writes,

            biomeHints = biomeData.grid,
            biomeResolution = biomeData.resolution,
            biomes = config.biomeDataManager.GetBiomeDefinitions(),
            lsystemTrees = config.biomeDataManager.GetLSystemTreeData(),
            builtinTreeRulesets = TreeRulesets.GetNativeTable(),
            decorations = config.biomeDataManager.GetDecorationData(),
            spawnBlocks = config.biomeDataManager.GetDecorationSpawnBlocks(),
            waterLevel = config.biomeDataManager.GetWaterLevel()
        };

        Profiler.StartDeco();
        JobHandle handle = job.Schedule();
        jobHandles[coord] = handle;
        outputLists[coord] = writes;
        inputs[coord] = (blockIds, lod);
        OnDecorationStarted?.Invoke(coord);
    }

    public void Update()
    {
        if (jobHandles.Count == 0)
            return;

        // Completing hands the chunk to ChunkManager (writes, open faces, frontier) on the main
        // thread, so outside bootstrap it is limited to a time budget per frame.
        bool unlimited = config.GetBootstrapMode();
        long budgetTicks = (long)(config.GetCompleteBudgetMs() * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        int completes = 0;

        PrepareKeyBuffer();

        for (int i = 0; i < keyCount; i++)
        {
            if (!unlimited && completes > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - start >= budgetTicks)
                break;

            int3 coord = keyBuffer[i];
            var handle = jobHandles[coord];

            if (!handle.IsCompleted)
                continue;

            handle.Complete();
            Profiler.EndDeco();
            completes++;

            var writesNative = outputLists[coord];
            jobHandles.Remove(coord);
            outputLists.Remove(coord);
            inputs.Remove(coord);

            // IMPORTANT: we do NOT dispose here.
            // Ownership of writesNative transfers to ChunkManager.
            OnDecorationCompleted?.Invoke(coord, writesNative);
        }
    }
    void PrepareKeyBuffer()
    {
        if (jobHandles.Count > keyBuffer.Length)
            keyBuffer = new int3[jobHandles.Count * 2]; // expands only rarely

        keyCount = 0;
        foreach (var kv in jobHandles)
            keyBuffer[keyCount++] = kv.Key;
    }
}
