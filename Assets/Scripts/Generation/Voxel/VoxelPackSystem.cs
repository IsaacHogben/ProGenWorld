using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Schedules VoxelPackJobs (block array -> GPU brick format) and hands finished results to
/// the ChunkManager. Takes the place of the old MeshSystem.
/// </summary>
public class VoxelPackSystem : IDisposable
{
    /// <summary>Mark the air next to leaves for the shader's leaf planes (off only for measuring).</summary>
    public static bool LeafMarkers = true;

    public struct Config
    {
        public int chunkSize;
        public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
        public Func<int> GetMaxUploadsPerFrame;
        public Func<LODLevel, int> GetMinLevel;   // finest GPU detail level a LOD uploads (null = all)
        public Func<LODLevel, int> GetSampleRes;  // blocks per voxel of a LOD's block arrays (null = 1)
    }

    struct PackJob
    {
        public JobHandle handle;
        public VoxelPackResult result;
        public int version;
        public NativeArray<byte> ownedBlocks;   // disposed once the job is done (default = caller's array)
    }

    struct PendingRequest
    {
        public NativeArray<byte> blocks;
        public LODLevel lod;
        public bool owned;
    }

    Config config;
    Func<int3, bool> canPackNow;
    Action<int3> markPacked;

    readonly Dictionary<int3, PackJob> jobs = new();
    readonly Dictionary<int3, PendingRequest> pending = new();
    readonly Dictionary<int3, int> versions = new();
    readonly Queue<(int3 coord, VoxelPackResult result, int version)> completed = new();
    readonly Stack<VoxelPackResult> resultPool = new();
    readonly List<int3> tmpKeys = new(256);

    int maxBricks;
    int maxCells;

    public int ActiveJobs => jobs.Count;
    public int PendingCount => pending.Count + completed.Count;

    public event Action<int3, VoxelPackResult> OnChunkPacked;

    public void Initialize(Config cfg, Func<int3, bool> canPackNow, Action<int3> markPacked)
    {
        config = cfg;
        this.canPackNow = canPackNow;
        this.markPacked = markPacked;

        maxBricks = VoxelGpuFormat.TotalBricks(cfg.chunkSize);
        maxCells = VoxelGpuFormat.MaxCells(cfg.chunkSize);
    }

    public bool IsPackInProgress(int3 coord) => jobs.ContainsKey(coord);

    /// <summary>A pack is running or waiting for this chunk (either one holds its block array).</summary>
    public bool IsPackQueued(int3 coord) => jobs.ContainsKey(coord) || pending.ContainsKey(coord);

    /// <summary>
    /// Requests a (re)pack of a chunk. Requests that arrive while a pack is running, or inside
    /// the debounce window, are kept and run as soon as possible instead of being dropped.
    /// </summary>
    /// <param name="takeOwnership">The pack system disposes the block array when it is done with
    /// it (arrays nobody else keeps, e.g. Horizon chunks' low resolution blocks).</param>
    public void RequestPack(int3 coord, NativeArray<byte> blocks, LODLevel lod, bool takeOwnership = false)
    {
        DropPending(coord);
        if (jobs.ContainsKey(coord) || !canPackNow(coord))
        {
            pending[coord] = new PendingRequest { blocks = blocks, lod = lod, owned = takeOwnership };
            return;
        }

        Schedule(coord, blocks, lod, takeOwnership);
    }

    void DropPending(int3 coord)
    {
        if (pending.Remove(coord, out var old) && old.owned && old.blocks.IsCreated)
            old.blocks.Dispose();
    }

    /// <summary>Drops any queued or running work for a chunk that is being unloaded.</summary>
    public void Cancel(int3 coord)
    {
        DropPending(coord);
        versions[coord] = Version(coord) + 1;

        // The block array is about to be disposed - make sure no job is still reading it.
        if (jobs.TryGetValue(coord, out var job))
        {
            job.handle.Complete();
            Profiler.EndMesh();
            jobs.Remove(coord);
            if (job.ownedBlocks.IsCreated) job.ownedBlocks.Dispose();
            ReturnResult(job.result);
        }
    }

    public void Update()
    {
        if (jobs.Count > 0)
        {
            tmpKeys.Clear();
            tmpKeys.AddRange(jobs.Keys);

            foreach (var coord in tmpKeys)
            {
                var job = jobs[coord];
                if (!job.handle.IsCompleted)
                    continue;

                job.handle.Complete();
                Profiler.EndMesh();
                jobs.Remove(coord);
                if (job.ownedBlocks.IsCreated) job.ownedBlocks.Dispose();
                completed.Enqueue((coord, job.result, job.version));
            }
        }

        if (pending.Count > 0)
        {
            tmpKeys.Clear();
            tmpKeys.AddRange(pending.Keys);

            foreach (var coord in tmpKeys)
            {
                if (jobs.ContainsKey(coord) || !canPackNow(coord))
                    continue;

                var request = pending[coord];
                pending.Remove(coord);
                if (request.blocks.IsCreated)
                    Schedule(coord, request.blocks, request.lod, request.owned);
            }
        }

        int budget = Math.Max(1, config.GetMaxUploadsPerFrame());
        while (completed.Count > 0 && budget > 0)
        {
            var (coord, result, version) = completed.Dequeue();

            if (version == Version(coord))
            {
                markPacked(coord);
                OnChunkPacked?.Invoke(coord, result);
                budget--;
            }

            ReturnResult(result);
        }
    }

    void Schedule(int3 coord, NativeArray<byte> blocks, LODLevel lod, bool owned)
    {
        int sampleRes = config.GetSampleRes != null ? config.GetSampleRes(lod) : 1;

        var result = RentResult();
        result.lod = lod;

        var job = new VoxelPackJob
        {
            blocks = blocks,
            blockDb = config.blockDb,
            leafMarkers = LeafMarkers,
            chunkSize = config.chunkSize,
            coord = coord,
            collectECSSpawns = lod == LODLevel.Near,
            minLevel = config.GetMinLevel != null ? config.GetMinLevel(lod) : 0,
            inputLevel = math.tzcnt(math.max(1, sampleRes)),
            bricks = result.bricks,
            pages = result.pages,
            meta = result.meta,
            cells = result.cells,
            ecsSpawns = result.ecsSpawns
        };

        Profiler.StartMesh();
        jobs[coord] = new PackJob
        {
            handle = job.Schedule(),
            result = result,
            version = Version(coord),
            ownedBlocks = owned ? blocks : default
        };
    }

    int Version(int3 coord) => versions.TryGetValue(coord, out int v) ? v : 0;

    VoxelPackResult RentResult()
    {
        if (resultPool.Count > 0)
            return resultPool.Pop();
        return VoxelPackResult.Create(maxBricks, maxCells);
    }

    void ReturnResult(VoxelPackResult result)
    {
        if (resultPool.Count < 64)
            resultPool.Push(result);
        else
            result.Dispose();
    }

    public void Dispose()
    {
        foreach (var job in jobs.Values)
        {
            job.handle.Complete();
            if (job.ownedBlocks.IsCreated) job.ownedBlocks.Dispose();
            job.result.Dispose();
        }
        jobs.Clear();

        foreach (var request in pending.Values)
            if (request.owned && request.blocks.IsCreated)
                request.blocks.Dispose();

        while (completed.Count > 0)
            completed.Dequeue().result.Dispose();

        while (resultPool.Count > 0)
            resultPool.Pop().Dispose();

        pending.Clear();
    }
}
