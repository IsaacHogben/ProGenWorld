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
    }

    private DecorationConfig cfg;

    // Per-chunk job tracking
    private readonly Dictionary<int3, JobHandle> jobHandles = new();
    private readonly Dictionary<int3, NativeList<PendingBlockWrite>> outputLists = new();
    private readonly Dictionary<int3, (NativeArray<byte> blockIds, LODLevel lod)> inputs = new();
    // Large static buffer reused for decoration write results.
    // Auto-resizes but NEVER allocates per-frame once stable.
    private static PendingBlockWrite[] sharedWriteBuffer = new PendingBlockWrite[256];

    public int ActiveJobs => jobHandles.Count;

    // Events
    public event Action<int3> OnDecorationStarted;
    public event Action<int3, PendingBlockWrite[], int> OnDecorationCompleted;

    public void Initialize(DecorationConfig config)
    {
        cfg = config;
    }

    public void ScheduleDecoration(int3 coord, LODLevel lod, NativeArray<byte> blockIds)
    {
        // Create output container
        var writes = new NativeList<PendingBlockWrite>(Allocator.Persistent);

        // Build job
        var job = new DecorationJob
        {
            chunkCoord = coord,
            chunkSize = cfg.chunkSize,
            indexSize = cfg.indexSize,
            lod = lod,
            seed = (uint)(cfg.seed ^ coord.GetHashCode()),
            blockIds = blockIds,
            pendingWrites = writes
        };

        Profiler.StartDeco();
        JobHandle handle = job.Schedule();

        // Store tracking
        jobHandles[coord] = handle;
        outputLists[coord] = writes;
        inputs[coord] = (blockIds, lod);

        // Fire start event
        OnDecorationStarted?.Invoke(coord);
    }

    public void Update()
    {
        if (jobHandles.Count == 0)
            return;

        // ------------------------------------------
        // Reuse temp keys list (no allocations)
        // ------------------------------------------
        tmpKeys.Clear();
        foreach (var kvp in jobHandles)
            tmpKeys.Add(kvp.Key);

        int maxDecorationCompletesPerFrame = 3;
        int completes = 0;

        // ------------------------------------------
        // Process completed jobs
        // ------------------------------------------
        for (int i = 0; i < tmpKeys.Count; i++)
        {
            if (completes >= maxDecorationCompletesPerFrame)
                break;

            var coord = tmpKeys[i];
            var handle = jobHandles[coord];

            if (!handle.IsCompleted)
                continue;

            handle.Complete();
            Profiler.EndDeco();
            completes++;

            // --------------------------------------
            // Convert NativeList to static buffer
            // --------------------------------------
            var writesNative = outputLists[coord];

            int count = writesNative.Length;

            // Ensure pooled array is large enough
            if (sharedWriteBuffer.Length < count)
                sharedWriteBuffer = new PendingBlockWrite[Mathf.NextPowerOfTwo(count)];

            // Copy NativeList to array
            for (int w = 0; w < count; w++)
                sharedWriteBuffer[w] = writesNative[w];

            // --------------------------------------
            // Free native containers
            // --------------------------------------
            if (writesNative.IsCreated)
                writesNative.Dispose();

            jobHandles.Remove(coord);
            outputLists.Remove(coord);
            inputs.Remove(coord);

            // --------------------------------------
            // Fire callback
            // --------------------------------------
            OnDecorationCompleted?.Invoke(coord, sharedWriteBuffer, count);
        }
    }


    // temp list avoid GC
    private static readonly List<int3> tmpKeys = new();
}
