using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
    public int ActiveJobs => jobHandles.Count;

    // Events
    public event Action<int3> OnDecorationStarted;
    public event Action<int3, List<PendingBlockWrite>> OnDecorationCompleted;

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
        // Collect ready jobs
        tmpKeys.Clear();
        tmpKeys.AddRange(jobHandles.Keys);

        foreach (var coord in tmpKeys)
        {
            var handle = jobHandles[coord];
            if (!handle.IsCompleted)
                continue;
            
            handle.Complete();
            Profiler.EndDeco();

            // Extract results
            var writesNative = outputLists[coord];
            var input = inputs[coord];

            // Convert NativeList to managed List for ChunkManager
            var results = new List<PendingBlockWrite>(writesNative.Length);
            for (int i = 0; i < writesNative.Length; i++)
                results.Add(writesNative[i]);

            // Cleanup internal containers
            if (writesNative.IsCreated) writesNative.Dispose();

            jobHandles.Remove(coord);
            outputLists.Remove(coord);
            inputs.Remove(coord);

            // Emit event
            OnDecorationCompleted?.Invoke(coord, results);
        }
    }

    // temp list avoid GC
    private static readonly List<int3> tmpKeys = new();
}
