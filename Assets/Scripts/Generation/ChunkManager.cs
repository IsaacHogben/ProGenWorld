// ChunkManager.cs
// High-level orchestration for chunk generation, LOD updates, and async job scheduling.
// This file manages chunk lifecycle, noise tasks, block assignment jobs, mesh jobs,
// pooling, and flood-fill terrain expansion.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static GreedyMeshJob;
using static UnityEngine.EventSystems.EventTrigger;

public class ChunkManager : MonoBehaviour
{
    // --------------------------------------------------
    // World Configuration
    // --------------------------------------------------

    [Header("World")]
    public int seed = 1337;
    public float frequency = 1;
    public int chunkSize = 32;
    public int viewDistance = 3;
    public int unloadPastViewDistance = 1;
    private int unloadDistance => viewDistance + unloadPastViewDistance;
    [SerializeField] int minMoveBeforeUpdate = 16;
    [SerializeField] RenderShape renderShape = RenderShape.Sphere;
    public bool unloadAtMaxChunks = false;
    [SerializeField] int maxChunks;         // Max chunks loaded before most distant chunks begin to unload
    [SerializeField] int chunkUnloadBuffer; // How many more chunks than max before chunk unload begins - prevents chunk flicker

    [Header("References")]
    public GameObject player;
    [SerializeField] private BlockDatabase blockDatabase;
    [SerializeField] public BlockTextureArrayBuilder textureArrayBuilder;
    [SerializeField] public Material voxelMaterial;

    [Header("LOD Settings")]
    [SerializeField] float nearRange = 4f;
    [SerializeField] float midRange = 10f;
    [SerializeField] float farRange = 20f;

    [SerializeField] Material nearMaterial;
    [SerializeField] Material midMaterial;
    [SerializeField] Material farMaterial;

    // References
    NoiseGenerator noiseGenerator;

    // --------------------------------------------------
    // Performance Tuning
    // --------------------------------------------------

    [Header("Performance tuning")]
    public int maxConcurrentNoiseTasks = 4;   // How many noise tasks to allow concurrently - Increase until CPU saturates or frame drops.
    public int maxMeshUploadsPerFrame = 2;    // How many mesh uploads (Mesh.SetVertices/SetTriangles) per frame - Increase until GPU upload hitching appears.
    public int minConcurrentNoiseTasks = 2;
    public int minMeshUploadsPerFrame = 2;
    public float scheduleInterval = 1f;       // Seconds between scheduling passes - Lower until chunk popping delay is acceptable.
    [SerializeField] int meshDebounceFrames = 2;// Minimum # of frames between mesh jobs for any given chunk

    [Header("Pre-allocation tuning")]
    // Configurable pool sizes (tweakable)
    [SerializeField] int maxDensityPoolPerLod = 32;
    [SerializeField] int prewarmDensityPerLod = 8;
    [SerializeField] int prewarmChunks = 200;
    [SerializeField] int prewarmMeshes = 400;

    // --- Adaptive performance control ---
    float avgFrameTime = 16.6f;
    float lastPerfAdjustTime = 0f;

    // --- Performance diagnostics ---
    public float AvgFrameTime => avgFrameTime;
    public int CurrentNoiseTasks => maxConcurrentNoiseTasks;
    public int CurrentMeshUploads => maxMeshUploadsPerFrame;
    public float CurrentScheduleInterval => scheduleInterval;
    public int CurrentPoolSize => 404;

    // --------------------------------------------------
    // Data Structures / State
    // --------------------------------------------------
    // Internal chunk tracking
    readonly Dictionary<int3, Chunk> chunks = new();
    readonly HashSet<int3> generatingSet = new();   // coords reserved for generation or meshing
    readonly Dictionary<LODLevel, LODSettings> lodConfigs = new();
    readonly Dictionary<int3, NativeArray<byte>> fullResBlocksByChunk = new Dictionary<int3, NativeArray<byte>>();
    readonly Dictionary<int3, List<PendingBlockWrite>> pendingWritesByChunk = new Dictionary<int3, List<PendingBlockWrite>>();
    readonly Dictionary<int3, int> lastMeshFrame = new();
    readonly HashSet<int3> chunksNeedingRemesh = new HashSet<int3>(); // Chunks that need their mesh rebuilt this frame
    readonly HashSet<int3> chunksStillDecorating = new HashSet<int3>(); // Tracks chunks currently executing a DecorationJob
    readonly HashSet<int3> meshInProgress = new HashSet<int3>(); // Tracks chunks currently executing a MeshJob

    // Threading / queues
    readonly ConcurrentQueue<(int3 coord, NativeArray<float> density, LODLevel lod)> completedNoiseQueue = new();
    SemaphoreSlim noiseLimiter = null;
    readonly HashSet<int3> activeNoiseTasks = new();

    // Job handles and pending work
    private readonly List<(int3 coord, JobHandle handle, NativeArray<float> density, NativeArray<byte> blockIds, LODLevel lod)> pendingBlockJobs = new(); // Waiting for block ID generation
    private readonly List<(JobHandle handle, MeshData meshData, NativeArray<byte> blockIds, bool keepBlockIds)> pendingMeshJobs = new(); // Waiting for mesh completion
    private readonly List<(int3 coord, JobHandle handle, NativeArray<byte> blockIds, NativeList<PendingBlockWrite> outWrites, LODLevel lod)> pendingDecorationJobs = new();
    readonly Queue<MeshData> meshApplyQueue = new();  // Meshes ready to apply on main thread

    // Tokens
    CancellationTokenSource cts;

    // --------------------------------------------------
    // Flood-Fill System
    // --------------------------------------------------
    readonly Queue<int3> frontierQueue = new();                 // chunks waiting to be expanded
    readonly HashSet<int3> frontierSet = new();                 // hash set for duplication checks

    struct LodUpdateRequest
    {
        public int3 coord;
        public LODLevel lod;
    }

    readonly Queue<LodUpdateRequest> lodUpdateQueue = new();    // chunks waiting to have lod updated
    readonly HashSet<int3> lodUpdateSet = new();                // hash set for duplication checks
    readonly HashSet<int3> deferredFrontier = new();            // neighbors to expand later (out of range)

    private bool firstFloodTriggered = false;
    public HashSet<int3> GetDeferredFrontier() => deferredFrontier;

    // Reusable temp buffers to avoid GC
    static readonly List<int3> tmpFrontierList = new List<int3>(512);
    static readonly List<int3> tmpFrontierRemove = new List<int3>(128);
    static readonly List<int3> tmpChunkKeys = new List<int3>(2048);
    static readonly List<int3> tmpChunksToRemove = new List<int3>(256);

    // --------------------------------------------------
    // Resources / Materials
    // --------------------------------------------------
    [Header("Resources Materials")]
    private NativeArray<BlockDatabase.BlockInfoUnmanaged> nativeBlockDatabase;
    private OpenFaceDetector openFaceDetector;  // kernel

    // --------------------------------------------------
    // Pooling System
    // --------------------------------------------------
    readonly Dictionary<LODLevel, Stack<NativeArray<float>>> densityPoolByLod =
        new Dictionary<LODLevel, Stack<NativeArray<float>>>();
    readonly object densityPoolLock = new();
    readonly Stack<GameObject> chunkGoPool = new();
    public Stack<Mesh> meshPool = new();

    // --------------------------------------------------
    // Timing / Movement Updates
    // --------------------------------------------------
    float lastScheduleTime = -999f;
    private bool movementTriggered = false;
    private int updatePhase = 0;
    private Vector3 lastPlayerPos;

    void Awake()
    {
        cts = new CancellationTokenSource();
        // Throttle the number of concurrent noise tasks:
        noiseLimiter = new SemaphoreSlim(Math.Max(1, maxConcurrentNoiseTasks), Math.Max(1, maxConcurrentNoiseTasks));
        // create noise generator
        noiseGenerator = new NoiseGenerator(seed);
        // create blockDatabase Native
        nativeBlockDatabase = blockDatabase.ToNative(Allocator.Persistent);
        // load texture array
        voxelMaterial.SetTexture("_Textures", textureArrayBuilder.GetTextureArray());
        // create openfacedetector (Can be moved to a computeShaderManager when we are using more)
        openFaceDetector = new OpenFaceDetector(ref nativeBlockDatabase);
        
        //Define LODs
        lodConfigs[LODLevel.Near] = new LODSettings { meshRes = 1, sampleRes = 1, blockSize = 1, detailedBlocks = true, material = voxelMaterial};
        lodConfigs[LODLevel.Mid] = new LODSettings { meshRes = 2, sampleRes = 1, blockSize = 2, detailedBlocks = false, material = midMaterial};
        lodConfigs[LODLevel.Far] = new LODSettings { meshRes = 1, sampleRes = 4, blockSize = 4, detailedBlocks = false, material = farMaterial};
        // Compute density counts per LOD
        foreach (var kv in lodConfigs.ToArray())
        {
            var lod = kv.Key;
            var cfg = kv.Value;

            int lodChunkSize = chunkSize / cfg.sampleRes;
            cfg.densityCount = (lodChunkSize + 1) * (lodChunkSize + 1) * (lodChunkSize + 1);

            lodConfigs[lod] = cfg;
        }
        InitDensityPools();
        PrewarmPools(); 
    }

    void Start()
    {
        // Seed frontier at the player's current chunk
        InitFloodFrontier(player.transform.position, 0, 10);
    }

    void Update()
    {
        // PROFILER: live stat updates each frame
        ChunkProfiler.ReportNoiseCount(maxConcurrentNoiseTasks - noiseLimiter.CurrentCount);
        ChunkProfiler.ReportUploadQueue(meshApplyQueue.Count + generatingSet.Count);
        ChunkProfiler.ReportChunkCount(chunks.Count);
        ChunkProfiler.ReportMeshCount(pendingMeshJobs.Count);
        //ChunkMemDebug.LogIfNeeded();
        /*if (Time.frameCount % 300 == 0)
        {
            Debug.Log($"[ChunkStats] chunks={chunks.Count} " +
                      $"chunkPool={chunkGoPool.Count} meshPool={meshPool.Count} " +
                      $"frontier={frontierQueue.Count} deferred={deferredFrontier.Count} " +
                      $"lodUpdateQ={lodUpdateQueue.Count} lodUpdateSet={lodUpdateSet.Count} " +
                      $"pendingBlock={pendingBlockJobs.Count} pendingMesh={pendingMeshJobs.Count} " +
                      $"activeNoise={activeNoiseTasks.Count}");
        }*/
        // --- Flood-fill activation when entering new chunk ---
        Vector3 currentPlayerPos = player.transform.position;
        float moved = math.distance(currentPlayerPos, lastPlayerPos);

        // Periodic scheduling (throttled)
        if (Time.time - lastScheduleTime >= scheduleInterval)
        {
            lastScheduleTime = Time.time;

            if (frontierQueue.Count > 0) 
                ScheduleNoiseTask(currentPlayerPos);
        }

        if (moved >= minMoveBeforeUpdate)
        {
            lastPlayerPos = currentPlayerPos;
            movementTriggered = true;
        }

        // Process phases only when needed
        if (movementTriggered)
            OnPlayerMovePhaseUpdate(lastPlayerPos);

        if (!firstFloodTriggered)
        {
            firstFloodTriggered = true;
            PromoteDeferredToFrontier(lastPlayerPos);
        }

        // Manage completed noise tasks, schedules block assignment
        while (completedNoiseQueue.TryDequeue(out var data))
        {
            // Skip if chunk is already irrelevant
            if (!IsChunkWithinRange(data.coord, lastPlayerPos, unloadDistance))
            {
                // Dispose density immediately
                if (data.density.IsCreated)
                    ReturnDensityArray(data.lod, data.density);
                continue;
            }

            var (handle, blockIds) = CreateBlockAssignmentJob(
                data.density,
                data.coord,
                lodConfigs[data.lod].sampleRes);

            pendingBlockJobs.Add((data.coord, handle, data.density, blockIds, data.lod));
        }

        // Check block assignment jobs
        for (int i = pendingBlockJobs.Count - 1; i >= 0; i--)
        {
            var it = pendingBlockJobs[i];
            if (!it.handle.IsCompleted) continue;

            it.handle.Complete();

            if (lodConfigs[it.lod].sampleRes == 1)
            {
                // If decoration already created a blockId array, we DO NOT replace it.
                if (fullResBlocksByChunk.TryGetValue(it.coord, out var existing) && existing.IsCreated)
                {
                    Debug.Log("Generation triggered for chunk that already has data");
                    /*// Copy terrain into the existing array
                    NativeArray<byte>.Copy(it.blockIds, existing);

                    // Dispose the temporary array created by the block job
                    it.blockIds.Dispose();*/
                }
                else
                {
                    // No existing array — this is the normal case for chunk generation
                    fullResBlocksByChunk[it.coord] = it.blockIds;
                }
            }

            // Decide if we should decorate this LOD (Near only)
            bool shouldDecorate = (lodConfigs[it.lod].sampleRes == 1);

            if (shouldDecorate)
            {
                // Create output list for cross-chunk pending writes
                var outWrites = new NativeList<PendingBlockWrite>(Allocator.Persistent);

                var decJob = new DecorationJob
                {
                    chunkCoord = it.coord,
                    chunkSize = chunkSize, // base size; Near => meshRes 1
                    indexSize = chunkSize + 1,
                    lod = it.lod,
                    seed = (uint)(seed ^ it.coord.GetHashCode()),
                    blockIds = it.blockIds,
                    pendingWrites = outWrites
                };

                var decHandle = decJob.Schedule();

                // Track decoration job instead of going directly to mesh
                chunksStillDecorating.Add(it.coord);
                pendingDecorationJobs.Add((it.coord, decHandle, it.blockIds, outWrites, it.lod));
            }
            else
            {
                // Flow detection + frontier expansion
                var faces = DetectTerrainFlowFromBlocks(it.blockIds, it.lod);
                ExpandFrontierFromFaces(it.coord, lastPlayerPos, faces);
                // Non-Near: no decoration, straight to mesh, dispose blockIds afterwards.
                ScheduleMeshJob(it.coord, it.blockIds, it.lod, false);
            }

            // Return density to pool
            if (it.density.IsCreated)
                ReturnDensityArray(it.lod, it.density);

            pendingBlockJobs.RemoveAt(i);
        }

        // Check decoration jobs (Near LOD chunks)
        for (int i = pendingDecorationJobs.Count - 1; i >= 0; i--)
        {
            var it = pendingDecorationJobs[i];
            if (!it.handle.IsCompleted) continue;

            it.handle.Complete();
            chunksStillDecorating.Remove(it.coord);

            // Merge cross-chunk writes into pendingWritesByChunk
            MergeDecorationOutputs(it.outWrites);

            // Flow detection + frontier expansion (unchanged)
            var faces = DetectTerrainFlowFromBlocks(it.blockIds, it.lod);
            ExpandFrontierFromFaces(it.coord, lastPlayerPos, faces);

            // Dispose NativeList from the job
            if (it.outWrites.IsCreated)
                it.outWrites.Dispose();

            // Do not mesh immediately — defer to centralized remesh sweep
            chunksNeedingRemesh.Add(it.coord);

            pendingDecorationJobs.RemoveAt(i);
        }

        FlushAllPendingWrites(lastPlayerPos);

        // Re-mesh all chunks that received neighbour writes this frame
        if (chunksNeedingRemesh.Count > 0)
        {
            tmpChunkKeys.Clear();
            tmpChunkKeys.AddRange(chunksNeedingRemesh);

            foreach (var coord in tmpChunkKeys)
            {
                // Must have blockIds
                if (!fullResBlocksByChunk.TryGetValue(coord, out var blockIds) || !blockIds.IsCreated)
                    continue;

                chunks.TryGetValue(coord, out var chunk);

                // For new chunks, lod is always Near (decoration only runs on Near)
                LODLevel lod = LODLevel.Mid;

                // If the chunk exists, respect its LOD
                if (chunk != null)
                    lod = chunk.lod;

                if (lod == LODLevel.Far)
                    continue;

                // Skip if it's mid-mesh (job in progress)
                if (meshInProgress.Contains(coord))
                    continue;

                // Debounce: only mesh if allowed this frame
                if (!CanMeshNow(coord))
                    continue;

                // OK schedule final remesh
                ScheduleMeshJob(coord, blockIds, lod, keepBlockIds: true);
                MarkMeshed(coord);
            }

            for (int i = pendingMeshJobs.Count - 1; i >= 0; i--)
            {
                var it = pendingMeshJobs[i];
                if (!it.handle.IsCompleted) continue;

                it.handle.Complete();
                // Only dispose blockIds if we are not keeping them for gameplay / future re-meshes
                if (!it.keepBlockIds)
                    DisposeBlockIds(it.blockIds);

                meshInProgress.Remove(it.meshData.coord);

                meshApplyQueue.Enqueue(it.meshData);
                pendingMeshJobs.RemoveAt(i);
            }

            chunksNeedingRemesh.Clear();
        }

        // Apply a limited number of meshes to Unity per frame
        int uploads = 0;
        while (uploads < maxMeshUploadsPerFrame && meshApplyQueue.Count > 0)
        {
            var meshData = meshApplyQueue.Dequeue();
            ApplyMeshDataToChunk(meshData);
            uploads++;
        }

        AdaptivePerformanceControl();
        if (Time.frameCount % 300 == 0)
        {
            //Debug.Log($"Chunks: {chunks.Count}, Deferred: {deferredFrontier.Count}, MeshPool: {meshPool.Count}, PendingBlock: {pendingBlockJobs.Count}, PendingMesh: {pendingMeshJobs.Count}");
        }
    }

    private void OnPlayerMovePhaseUpdate(Vector3 playerPos)
    {
        switch (updatePhase)
        {
            case 0:
                PromoteDeferredToFrontier(playerPos);
                break;
            case 1:
                UpdateChunkLods(playerPos);
                break;
            case 2:
                UnloadChunks(playerPos);
                break;
            case 3:
                CullOutOfRangeBlockJobs(playerPos);
                break;
        }

        updatePhase++;

        // Completed full cycle
        if (updatePhase > 3)
        {
            updatePhase = 0;
            movementTriggered = false;   // Done until next movement trigger
        }
    }
    void UpdateChunkLods(Vector3 playerPos)
    {
        List<int3> toPromoteNear = new(32);  
        List<int3> toPromoteMid = new(32);  

        foreach (var kv in chunks)
        {
            var coord = kv.Key;
            var chunk = kv.Value;
            if (chunk == null) continue;

            var chunkLod = chunk.lod;            
            var desiredLod = GetLODForCoord(coord, playerPos, chunkLod);
            if (desiredLod == chunkLod)
                continue;

            // Promote only (never lower LOD) ----------- Change here if we want LOD's to downgrade as well
            if (desiredLod < chunkLod)
            {
                if (chunkLod == LODLevel.Mid)
                    chunksNeedingRemesh.Add(coord);
                //EnqueueLodUpdate(kv.Key, LODLevel.Near);
                else if (chunkLod == LODLevel.Far) EnqueueLodUpdate(kv.Key, LODLevel.Mid);
                chunk.lod = desiredLod;
            }
        }
    }
    // flood-fill scheduler (closest-first implicitly due to frontier expansion pattern)
    void ScheduleNoiseTask(Vector3 playerPos)
    {
        if (player == null) return;

        int spawnedThisTick = 0;
        int cap = Math.Max(1, maxConcurrentNoiseTasks);

        // Here we are initiating a number of tasks that will eventually result in chunks
        // We are limiting this per tick and prioritizing LOD upgrades over new chunk generation.

        if (lodUpdateQueue.Count != 0)
            while (spawnedThisTick < cap && TryDequeueLodUpdate(out var lodUpdateToken))
            {
                if (!chunks.ContainsKey(lodUpdateToken.coord)) continue;
                generatingSet.Add(lodUpdateToken.coord);
                spawnedThisTick++;
                StartNoiseTask(lodUpdateToken.coord, lodUpdateToken.lod);
            }
        
        if (frontierQueue.Count != 0)
            while (spawnedThisTick < cap && TryDequeueFrontier(out var coord))
            {
                if (chunks.ContainsKey(coord) || generatingSet.Contains(coord)) continue;
                generatingSet.Add(coord);
                spawnedThisTick++;
                LODLevel lod = GetLODForCoord(coord, playerPos, LODLevel.Mid);
                StartNoiseTask(coord, lod);
            }
    }
    void ExpandFrontierFromFaces(int3 coord, Vector3 playerPos, OpenFaces faces)
    {
        if (faces == OpenFaces.None) return;

        Span<(OpenFaces face, int3 d)> dirs = stackalloc (OpenFaces, int3)[]
        {
            (OpenFaces.PosX, new int3( 1, 0, 0)),
            (OpenFaces.NegX, new int3(-1, 0, 0)),
            (OpenFaces.PosY, new int3( 0, 1, 0)),
            (OpenFaces.NegY, new int3( 0,-1, 0)),
            (OpenFaces.PosZ, new int3( 0, 0, 1)),
            (OpenFaces.NegZ, new int3( 0, 0,-1)),
        };

        foreach (var t in dirs)
        {
            if (!faces.HasFlag(t.face)) continue;

            int3 n = coord + t.d;

            if (chunks.ContainsKey(n) || generatingSet.Contains(n))
                continue;

            if (IsChunkWithinRange(n, playerPos, viewDistance))
            {
                EnqueueFrontier(n);
            }
            else
            {
                deferredFrontier.Add(n); // keep the wavefront memory
            }
        }
    }
    // Player changed chunk — promote deferred coords that are now in range
    void PromoteDeferredToFrontier(Vector3 playerPos)
    {
        // promote any deferred chunks now within view distance
        List<int3> toPromote = new(32);

        foreach (var c in deferredFrontier)
        {
            if (chunks.ContainsKey(c) || generatingSet.Contains(c))
                continue;
     
            if (IsChunkWithinRange(c, playerPos, viewDistance))
                toPromote.Add(c);
        }

        foreach (var c in toPromote)
        {
            EnqueueFrontier(c);
            deferredFrontier.Remove(c);
        }
    }
    void StartNoiseTask(int3 coord, LODLevel lod)
    {
        LODSettings lodSettings = lodConfigs[lod];

        lock (activeNoiseTasks)
        {
            if (activeNoiseTasks.Contains(coord))
                return;
            activeNoiseTasks.Add(coord);
        }

        ChunkProfiler.ReportNoiseCount(activeNoiseTasks.Count);

        // Rent buffer *once* here, owned by this task until enqueued
        NativeArray<float> densityBuffer = RentDensityArray(lod);

        Task.Run(async () =>
        {
            bool acquired = false;

            try
            {
                await noiseLimiter.WaitAsync(cts.Token);
                acquired = true;

                if (cts.IsCancellationRequested)
                    return;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                densityBuffer.CopyFrom(noiseGenerator.FillDensity(coord, chunkSize, frequency, lodSettings.sampleRes)); //Editing native array in asyn task is unsafe. Is currently accounted for.
                sw.Stop();

                completedNoiseQueue.Enqueue((coord, densityBuffer, lod));

                lock (ChunkProfilerTimes.noiseLock)
                    ChunkProfilerTimes.noiseDurations.Enqueue((float)sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // Dispose if cancelled before use
                if (densityBuffer.IsCreated)
                    ReturnDensityArray(lod, densityBuffer);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Noise task failed for {coord}: {ex}");

                if (densityBuffer.IsCreated)
                    ReturnDensityArray(lod, densityBuffer);
            }
            finally
            {
                if (acquired)
                    noiseLimiter.Release();

                lock (activeNoiseTasks)
                    activeNoiseTasks.Remove(coord);
            }
        });
    }
    private (JobHandle handle, NativeArray<byte> blockIds) CreateBlockAssignmentJob(NativeArray<float> nativeDensity, int3 coord, int sampleRes)
    {
        int chunkLodSize = chunkSize / sampleRes;
        int voxelCount = (chunkLodSize + 1) * (chunkLodSize + 1) * (chunkLodSize + 1);
        var blockIds = new NativeArray<byte>(voxelCount, Allocator.Persistent);
        ChunkMemDebug.ActiveBlockIdArrays++;
        ChunkMemDebug.TotalBlockIdAlloc++;

        var job = new BlockAssignmentJob
        {
            density = nativeDensity,
            blockIds = blockIds,
            chunkSize = chunkLodSize,
            startingCoord = coord,
        };

        var handle = job.Schedule(voxelCount, 64); // async, no Complete()
        return (handle, blockIds);
    }
    private OpenFaces DetectOpenFacesFromBlocks(NativeArray<byte> blockIds)
    {
        OpenFaces flags = OpenFaces.None;
        int s = chunkSize + 1;

        bool Solid(int x, int y, int z) => blockIds[x + y * s + z * s * s] == 0;

        // +X
        for (int y = 0; y < s; y++) for (int z = 0; z < s; z++) if (Solid(s - 1, y, z)) { flags |= OpenFaces.PosX; goto NEG_X; }
            NEG_X:
        // -X
        for (int y = 0; y < s; y++) for (int z = 0; z < s; z++) if (Solid(0, y, z)) { flags |= OpenFaces.NegX; goto POS_Y; }
            POS_Y:
        // +Y
        for (int x = 0; x < s; x++) for (int z = 0; z < s; z++) if (Solid(x, s - 1, z)) { flags |= OpenFaces.PosY; goto NEG_Y; }
            NEG_Y:
        // -Y
        for (int x = 0; x < s; x++) for (int z = 0; z < s; z++) if (Solid(x, 0, z)) { flags |= OpenFaces.NegY; goto POS_Z; }
            POS_Z:
        // +Z
        for (int x = 0; x < s; x++) for (int y = 0; y < s; y++) if (Solid(x, y, s - 1)) { flags |= OpenFaces.PosZ; goto NEG_Z; }
            NEG_Z:
        // -Z
        for (int x = 0; x < s; x++) for (int y = 0; y < s; y++) if (Solid(x, y, 0)) { flags |= OpenFaces.NegZ; }

        return flags;
    }
    private OpenFaces DetectTerrainFlowFromBlocks(NativeArray<byte> blockIds, LODLevel lod)
    {
        // determine meshRes based on LOD
        int meshRes = lod switch
        {
            LODLevel.Near => 1,
            LODLevel.Mid => 1,
            LODLevel.Far => 4,
            _ => 1
        };

        int s = (chunkSize / meshRes) + 1;
        OpenFaces flags = OpenFaces.None;

        bool IsSolid(int x, int y, int z)
            => blockIds[x + y * s + z * s * s] != 0; // non-air = solid

        bool FaceExposed(Func<int, int, bool> test)
        {
            bool sawSolid = false;
            bool sawAir = false;

            // sample every Nth voxel depending on LOD meshRes
            for (int i = 0; i < s; i += meshRes)
            {
                for (int j = 0; j < s; j += meshRes)
                {
                    bool solid = test(i, j);
                    if (solid) sawSolid = true; else sawAir = true;

                    if (sawSolid && sawAir)
                        return true; // mixed = exposed
                }
            }
            return false; // fully solid or fully air = not exposed
        }

        // +X
        if (FaceExposed((y, z) => IsSolid(s - 1, y, z)))
            flags |= OpenFaces.PosX;

        // -X
        if (FaceExposed((y, z) => IsSolid(0, y, z)))
            flags |= OpenFaces.NegX;

        // +Y
        if (FaceExposed((x, z) => IsSolid(x, s - 1, z)))
            flags |= OpenFaces.PosY;

        // -Y
        if (FaceExposed((x, z) => IsSolid(x, 0, z)))
            flags |= OpenFaces.NegY;

        // +Z
        if (FaceExposed((x, y) => IsSolid(x, y, s - 1)))
            flags |= OpenFaces.PosZ;

        // -Z
        if (FaceExposed((x, y) => IsSolid(x, y, 0)))
            flags |= OpenFaces.NegZ;

        return flags;
    }
    void ScheduleMeshJob(int3 coord, NativeArray<byte> blockIds, LODLevel lod, bool keepBlockIds)
    {
        ChunkProfiler.MeshStart(); // Wont track an async job. Add handle.Complete() to profile

        var meshData = new MeshData
        {
            coord = coord,
            vertices = new NativeList<float3>(Allocator.Persistent),
            triangles = new NativeList<int>(Allocator.Persistent),
            normals = new NativeList<float3>(Allocator.Persistent),
            colors = new NativeList<float4>(Allocator.Persistent),
            UV0s = new NativeList<float2>(Allocator.Persistent),
            meshRes = lodConfigs[lod].meshRes,
            lod = lod
        };
        ChunkMemDebug.ActiveMeshDatas++;
        ChunkMemDebug.TotalMeshDataAlloc++;

        var job = new GreedyMeshJob
        {
            blockArray = blockIds,
            blocks = nativeBlockDatabase,
            chunkSize = chunkSize / lodConfigs[lod].sampleRes,
            blockSize = lodConfigs[lod].blockSize,
            meshData = meshData,
        };

        JobHandle handle = job.Schedule();
        pendingMeshJobs.Add((handle, meshData, blockIds, keepBlockIds));
        meshInProgress.Add(meshData.coord);
        //handle.Complete();
        ChunkProfiler.MeshEnd();
    }
    void ApplyMeshDataToChunk(MeshData meshData)
    {
        ChunkProfiler.UploadStart();

        bool chunkEmpty = !meshData.vertices.IsCreated || meshData.vertices.Length == 0;
        chunks.TryGetValue(meshData.coord, out var chunk);

        if (!chunkEmpty && chunk == null) // Do these things only to new chunks
        {
            GameObject go;
            if (chunkGoPool.Count > 0)
            {
                go = chunkGoPool.Pop();
                go.SetActive(true);
            }
            else
            {
                go = new GameObject();
                go.AddComponent<Chunk>();
            }

            go.name = $"Chunk_{meshData.coord.x}_{meshData.coord.y}_{meshData.coord.z}";
            go.transform.position = ChunkToWorld(meshData.coord);
            go.transform.parent = transform;

            chunk = go.GetComponent<Chunk>();
            
            chunks[meshData.coord] = chunk;
        }

        if (chunk != null) // Do these things to all chunks 
        { 
            chunk.chunkMaterial = voxelMaterial;
            chunk.lod = meshData.lod;
            chunk.ApplyMesh(meshData, meshPool); 
        }

        
        generatingSet.Remove(meshData.coord);
        deferredFrontier.Remove(meshData.coord);
        DisposeMeshData(ref meshData);

        ChunkProfiler.UploadEnd();
    }
    void AdaptivePerformanceControl()
    {
        avgFrameTime = Mathf.Lerp(avgFrameTime, Time.deltaTime * 1000f, 0.1f);

        // Adaptive performance adjustion frequency
        if (Time.time - lastPerfAdjustTime < 0.1f)
            return;

        lastPerfAdjustTime = Time.time;

        const float dangerFrameTime = 18f;     // ~?60 FPS
        const float safeFrameTime = 8.3333f; // ?120 FPS

        bool adjusted = false;

        if (avgFrameTime > dangerFrameTime)
        {
            // Decrease load
            maxConcurrentNoiseTasks = Mathf.Max(minConcurrentNoiseTasks, maxConcurrentNoiseTasks - 1);
            maxMeshUploadsPerFrame = Mathf.Max(minMeshUploadsPerFrame, maxMeshUploadsPerFrame - 1);
            scheduleInterval = Mathf.Min(scheduleInterval * 1.15f, 0.4f);
            adjusted = true;
        }
        else if (avgFrameTime < safeFrameTime)
        {
            // Increase load
            maxConcurrentNoiseTasks = Mathf.Min(maxConcurrentNoiseTasks + 1, SystemInfo.processorCount);
            maxMeshUploadsPerFrame = Mathf.Min(maxMeshUploadsPerFrame + 1, minMeshUploadsPerFrame);
            scheduleInterval = Mathf.Max(scheduleInterval * 0.85f, 0.05f);
            adjusted = true;
        }

        if (adjusted)
        {
            //Debug.Log($"[PerfTuner] {avgFrameTime:F1}ms ? Noise={maxConcurrentNoiseTasks}, Uploads={maxMeshUploadsPerFrame}, Interval={scheduleInterval:F2}");
        }
    }
    // Create an initial zone around the player from which the flood fill can start
    void InitFloodFrontier(Vector3 playerPos, short radius, int height) // Create the initial position for flood fill to begin. Is the players location at start of world load.
    {
        int3 centerChunk = WorldToChunkCoord(playerPos);
        float sqrViewDist = radius * radius;

        for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
                for (int y = -height; y <= height; y++)
                {
                    float distSqr = x * x + y * y + z * z;
                    if (distSqr > sqrViewDist)
                        continue;

                    int3 coord = centerChunk + new int3(x, y, z);

                    // skip if chunk already exists or is generating
                    if (chunks.ContainsKey(coord) || generatingSet.Contains(coord))
                        continue;

                    EnqueueFrontier(coord);
                }

        // Immediately run a flood-fill tick (respects throttling)
        ScheduleNoiseTask(playerPos);
    }
    // Unloads chunks when the number of chunks in play exeeds the maxChunks value. Unloads based on furtheset distance from player
    void UnloadChunks(Vector3 currentPlayerChunk)
    {    
        if (deferredFrontier.Count > 0)
        {
            // Copy to list once to avoid hashset iteration overhead
            tmpFrontierList.Clear();
            tmpFrontierList.AddRange(deferredFrontier);

            tmpFrontierRemove.Clear();

            // Process deferred frontier in one tight loop
            int unloadPlusOne = unloadDistance + 1;
            for (int i = 0; i < tmpFrontierList.Count; i++)
            {
                int3 coord = tmpFrontierList[i];

                if (GetDistanceAtChunkScaleWithRenderShape(coord, currentPlayerChunk) > unloadPlusOne)
                    tmpFrontierRemove.Add(coord);
            }

            // Remove flagged items
            for (int i = 0; i < tmpFrontierRemove.Count; i++)
                deferredFrontier.Remove(tmpFrontierRemove[i]);

            // Evaluate real chunks only if deferred contained something
            tmpChunkKeys.Clear();
            tmpChunkKeys.AddRange(chunks.Keys);

            tmpChunksToRemove.Clear();

            for (int i = 0; i < tmpChunkKeys.Count; i++)
            {
                int3 coord = tmpChunkKeys[i];

                if (GetDistanceAtChunkScaleWithRenderShape(coord, currentPlayerChunk) > unloadDistance)
                    tmpChunksToRemove.Add(coord);
            }

            // Remove them
            for (int i = 0; i < tmpChunksToRemove.Count; i++)
                ReleaseChunk(tmpChunksToRemove[i]);
        }

        if (unloadAtMaxChunks && chunks.Count > maxChunks + chunkUnloadBuffer)
        {
            // Collect keys once
            tmpChunkKeys.Clear();
            tmpChunkKeys.AddRange(chunks.Keys);

            // Sort by distance descending (furthest first)
            tmpChunkKeys.Sort((a, b) =>
            {
                float da = GetDistanceAtChunkScaleWithRenderShape(a, currentPlayerChunk);
                float db = GetDistanceAtChunkScaleWithRenderShape(b, currentPlayerChunk);
                return db.CompareTo(da); // descending
            });

            for (int i = 0; i < tmpChunkKeys.Count; i++)
            {
                int3 coord = tmpChunkKeys[i];
                ReleaseChunk(coord);

                if (chunks.Count < maxChunks)
                    break;
            }
        }
    }

    // =============================
    // ===== HELPER FUNCTIONS ======
    // =============================
    bool CanMeshNow(int3 coord)
    {
        if (!lastMeshFrame.TryGetValue(coord, out int last))
            return true;

        return Time.frameCount - last >= meshDebounceFrames;
    }
    void MarkMeshed(int3 coord)
    {
        lastMeshFrame[coord] = Time.frameCount;
    }
    void FlushAllPendingWrites(Vector3 currentPlayerChunk)
    {
        if (pendingWritesByChunk.Count == 0)
            return;

        tmpChunkKeys.Clear();
        tmpChunkKeys.AddRange(pendingWritesByChunk.Keys);

        foreach (var coord in tmpChunkKeys)
        {
            // Skip if decoration or mesh is running
            if (chunksStillDecorating.Contains(coord))
                continue;
            if (meshInProgress.Contains(coord))
                continue;

            bool hasBlockIds =
                fullResBlocksByChunk.TryGetValue(coord, out var blockIds) &&
                blockIds.IsCreated;

            bool hasChunk = chunks.ContainsKey(coord);
            bool isGenerating = generatingSet.Contains(coord);

            // Trigger chunk gen for case where decorations spill into a chunk that hasn't generated because it didn't have any terrain blocks.
            if (!hasBlockIds && !hasChunk && !isGenerating && GetDistanceAtChunkScaleWithRenderShape(coord, currentPlayerChunk) < viewDistance)
            {
                if (!deferredFrontier.Contains(coord) && !frontierSet.Contains(coord))
                {
                    // Add the coordinate where the writes are placed into the defferedFrontier
                    // This will add the chunks to the queue to eventually run through the full pipline
                    deferredFrontier.Add(coord);
                }
                continue;
            }

            // Terrain exists safe to apply writes now
            if (hasBlockIds)
            {
                var writes = pendingWritesByChunk[coord];

                for (int i = 0; i < writes.Count; i++)
                    SafeApplyWrite(coord, writes[i]);

                pendingWritesByChunk.Remove(coord);
                chunksNeedingRemesh.Add(coord);
            }
            //Terrain not ready yet leave writes queued
        }
    }
    void SafeApplyWrite(int3 coord, PendingBlockWrite w)
    {
        // If chunk is decorating or mesh job is running or we dont have blocks yet
        // queue the write for later.
        if (chunksStillDecorating.Contains(coord) ||
            meshInProgress.Contains(coord) ||
            !fullResBlocksByChunk.TryGetValue(coord, out var blockIds) ||
            !blockIds.IsCreated)
        {
            if (!pendingWritesByChunk.TryGetValue(coord, out var waiting))
                pendingWritesByChunk[coord] = waiting = new List<PendingBlockWrite>();
            waiting.Add(w);
            return;
        }

        // Safe to apply now
        ApplySinglePendingWrite(coord, blockIds, w);
        chunksNeedingRemesh.Add(coord);
    }
    void ApplySinglePendingWrite(int3 coord, NativeArray<byte> blockIds, PendingBlockWrite w)
    {
        int s = chunkSize + 1;
        int3 p = w.localPos;

        if ((uint)p.x >= s || (uint)p.y >= s || (uint)p.z >= s)
            return;

        int index = p.x + p.y * s + p.z * s * s;
        byte current = blockIds[index];

        switch (w.mode)
        {
            case PendingWriteMode.Replace:
                blockIds[index] = w.blockId;
                break;

            case PendingWriteMode.ReplaceAir:
                if (current == 0)
                    blockIds[index] = w.blockId;
                break;

            case PendingWriteMode.ReplaceSoft:
                if (current == 0)
                    blockIds[index] = w.blockId;
                break;
        }

        if (!w.isMirror)
            EnqueueBoundaryMirrors(coord, w);
    }
    void EnqueueBoundaryMirrors(int3 coord, PendingBlockWrite source)
    {
        int3 p = source.localPos;
        int edge = chunkSize;

        void EnqueueMirror(int3 neighborChunk, int3 neighborLocal)
        {
            var mirror = new PendingBlockWrite
            {
                targetChunk = neighborChunk,
                localPos = neighborLocal,
                blockId = source.blockId,
                mode = source.mode,
                isMirror = true   // important: mirrors never spawn more mirrors
            };

            if (!pendingWritesByChunk.TryGetValue(neighborChunk, out var list))
                pendingWritesByChunk[neighborChunk] = list = new List<PendingBlockWrite>();

            list.Add(mirror);
        }

        // X faces
        if (p.x == 0)
            EnqueueMirror(coord + new int3(-1, 0, 0), new int3(edge, p.y, p.z));
        /*else if (p.x == edge)
            EnqueueMirror(coord + new int3(1, 0, 0), new int3(0, p.y, p.z));*/

        // Y faces
        if (p.y == 0)
            EnqueueMirror(coord + new int3(0, -1, 0), new int3(p.x, edge, p.z));
        /*else if (p.y == edge)
            EnqueueMirror(coord + new int3(0, 1, 0), new int3(p.x, 0, p.z));*/

        // Z faces
        if (p.z == 0)
            EnqueueMirror(coord + new int3(0, 0, -1), new int3(p.x, p.y, edge));
        /*else if (p.z == edge)
            EnqueueMirror(coord + new int3(0, 0, 1), new int3(p.x, p.y, 0));*/
    }
    void MergeDecorationOutputs(NativeList<PendingBlockWrite> writes)
    {
        for (int i = 0; i < writes.Length; i++)
            SafeApplyWrite(writes[i].targetChunk, writes[i]);
    }
    LODLevel GetLODForCoord(int3 coord, Vector3 playerChunk, LODLevel current)
    {
        float dist = GetDistanceAtChunkScaleWithRenderShape(coord, playerChunk);
        float h = 0.2f;

        switch (current)
        {
            case LODLevel.Near:
                if (dist > nearRange + h) return LODLevel.Mid;
                return LODLevel.Near;

            case LODLevel.Mid:
                if (dist < nearRange - h) return LODLevel.Near;
                if (dist > midRange + h) return LODLevel.Far;
                return LODLevel.Mid;

            case LODLevel.Far:
                if (dist < midRange - h) return LODLevel.Mid;
                return LODLevel.Far;
        }

        return current;
    }
    void EnqueueFrontier(int3 c)
    {
        if (frontierSet.Add(c))
        {
            frontierQueue.Enqueue(c);
        }
    }
    bool TryDequeueFrontier(out int3 c)
    {
        while (frontierQueue.Count > 0)
        {
            c = frontierQueue.Dequeue();
            if (frontierSet.Remove(c))
            {
                return true;
            }
        }
        c = default;
        return false;
    }
    void EnqueueLodUpdate(int3 c, LODLevel l) // Currently does not do any extra sorting
    {
        if (lodUpdateSet.Add(c)) lodUpdateQueue.Enqueue(new LodUpdateRequest {coord = c, lod = l});
    }
    bool TryDequeueLodUpdate(out LodUpdateRequest c)
    {
        while (lodUpdateQueue.Count > 0)
        {
            c = lodUpdateQueue.Dequeue();
            if (lodUpdateSet.Remove(c.coord)) return true; // real item
        }
        c = default; return false;
    }
    void CullOutOfRangeBlockJobs(Vector3 center)
    {
        for (int i = pendingBlockJobs.Count - 1; i >= 0; --i)
        {
            var it = pendingBlockJobs[i];
            if (!IsChunkWithinRange(it.coord, center, viewDistance + 1)) // small buffer
            {
                it.handle.Complete();
                ReturnDensityArray(it.lod, it.density);
                DisposeBlockIds(it.blockIds);
                pendingBlockJobs.RemoveAt(i);
                generatingSet.Remove(it.coord);
            }
        }
    }
    void ReleaseChunk(int3 coord)
    {
        deferredFrontier.Add(coord); // If a chunk is removed it must then become a new frontier for terrain generation
        if (!chunks.TryGetValue(coord, out var chunk) || chunk == null)
            return;

        chunk.Release(meshPool);
        chunkGoPool.Push(chunk.gameObject);
        chunks.Remove(coord);

        if (fullResBlocksByChunk.TryGetValue(coord, out var arr))
        {
            if (arr.IsCreated) arr.Dispose();
            fullResBlocksByChunk.Remove(coord);
        }

        // Also clear any “future” writes never applied
        pendingWritesByChunk.Remove(coord);
        chunksNeedingRemesh.Remove(coord);
    }
    static void DisposeMeshData(ref MeshData m)
    {
        if (m.vertices.IsCreated) m.vertices.Dispose();
        if (m.triangles.IsCreated) m.triangles.Dispose();
        if (m.normals.IsCreated) m.normals.Dispose();
        if (m.colors.IsCreated) m.colors.Dispose();
        if (m.UV0s.IsCreated) m.UV0s.Dispose();
        ChunkMemDebug.ActiveMeshDatas--;
    }   
    static void DisposeBlockIds(NativeArray<byte> arr)
    {
        ChunkMemDebug.ActiveBlockIdArrays--;
        if (arr.IsCreated)
            arr.Dispose();
    }
    public Vector3 ChunkToWorld(int3 coord) =>                                      // Public for debug visualizers
        new Vector3(coord.x, coord.y, coord.z) * chunkSize;    // Add .5f to better represent the center of the chunk rather than origin
    int3 WorldToChunkCoord(Vector3 pos)
    {
        return new int3(
            Mathf.FloorToInt(pos.x / chunkSize),
            Mathf.FloorToInt(pos.y / chunkSize),
            Mathf.FloorToInt(pos.z / chunkSize)
        );
    }
    void InitDensityPools()
        {
            densityPoolByLod.Clear();
            foreach (var lod in lodConfigs.Keys)
            {
                densityPoolByLod[lod] = new Stack<NativeArray<float>>();
            }
        }
    void PrewarmPools()
    {
        for (int i = 0; i < prewarmChunks; i++)
        {
            var go = new GameObject($"PooledChunk_{i}");
            go.SetActive(false);
            go.transform.parent = transform;

            var chunk = go.AddComponent<Chunk>();
            chunk.chunkMaterial = voxelMaterial;
            chunkGoPool.Push(go);
        }

        for (int i = 0; i < prewarmMeshes; i++)
        {
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.MarkDynamic(); // helps when reusing buffer content
            meshPool.Push(mesh);
        }

        // Density per LOD
        lock (densityPoolLock)
        {
            foreach (var kv in lodConfigs)
            {
                var lod = kv.Key;
                var cfg = kv.Value;

                var pool = densityPoolByLod[lod];

                while (pool.Count < Mathf.Min(prewarmDensityPerLod, maxDensityPoolPerLod))
                {
                    pool.Push(new NativeArray<float>(cfg.densityCount, Allocator.Persistent));
                    ChunkMemDebug.TotalDensityAlloc++;
                }
            }
        }
    }
    NativeArray<float> RentDensityArray(LODLevel lod)
    {
        var cfg = lodConfigs[lod];
        int requiredLength = cfg.densityCount;

        lock (densityPoolLock)
        {
            var pool = densityPoolByLod[lod];

            while (pool.Count > 0)
            {
                var arr = pool.Pop();
                if (!arr.IsCreated || arr.Length != requiredLength)
                {
                    if (arr.IsCreated) arr.Dispose();
                    continue;
                }

                ChunkMemDebug.ActiveDensityArrays++;
                return arr;
            }
        }

        // allocate new
        var allocated = new NativeArray<float>(requiredLength, Allocator.Persistent);
        ChunkMemDebug.ActiveDensityArrays++;
        ChunkMemDebug.TotalDensityAlloc++;
        return allocated;
    }
    void ReturnDensityArray(LODLevel lod, NativeArray<float> arr)
    {
        if (!arr.IsCreated) return;

        var cfg = lodConfigs[lod];
        if (arr.Length != cfg.densityCount)
        {
            arr.Dispose();
        }
        else
        {
            lock (densityPoolLock)
            {
                var pool = densityPoolByLod[lod];
                if (pool.Count >= maxDensityPoolPerLod)
                    arr.Dispose();
                else
                    pool.Push(arr);
            }
        }

        ChunkMemDebug.ActiveDensityArrays--;
    }
    public bool IsChunkWithinRange(int3 chunkCoord, Vector3 playerChunk, float viewDistance)
    {
        return GetDistanceAtChunkScaleWithRenderShape(chunkCoord, playerChunk) <= viewDistance;
    }
    float GetDistanceAtChunkScaleWithRenderShape(int3 _chunk, Vector3 pos)
    {
        Vector3 chunk = ChunkToWorld(_chunk);

        switch (renderShape)
        {
            case RenderShape.Cylinder:
                {
                    // 2D distance (XZ only)
                    float dx = chunk.x - pos.x;
                    float dz = chunk.z - pos.z;

                    float distSq = dx * dx + dz * dz;
                    float dist = math.sqrt(distSq);

                    return dist / chunkSize;
                }

            case RenderShape.Sphere:
            default:
                {
                    float dx = chunk.x - pos.x;
                    float dy = chunk.y - pos.y;
                    float dz = chunk.z - pos.z;

                    float distSq = dx * dx + dy * dy + dz * dz;
                    float dist = math.sqrt(distSq);

                    return dist / chunkSize;
                }
        }
    }
    void CompleteAllJobs()
    {
        foreach (var (coord, handle, density, blockIds, lod) in pendingBlockJobs.ToArray())
        {
            handle.Complete();
            if (density.IsCreated) ReturnDensityArray(lod, density);
            DisposeBlockIds(blockIds);
        }
        pendingBlockJobs.Clear();

        // Complete and dispose pending jobs safely
        for (int i = pendingMeshJobs.Count - 1; i >= 0; i--)
        {
            var (handle, meshData, blockIds, keepBlockIds) = pendingMeshJobs[i];
            if (!handle.IsCompleted) handle.Complete();

            // Dispose blockIds — no density arrays here anymore
            DisposeBlockIds(blockIds);

            // Dispose mesh native lists (if not already disposed by ApplyMesh)
            DisposeMeshData(ref meshData);

            pendingMeshJobs.RemoveAt(i);
        }
        pendingMeshJobs.Clear();
    }
    void OnDestroy()
    {
        // Stop async tasks
        CompleteAllJobs();
        cts?.Cancel();
        cts?.Dispose();

        // Dispose leftover pooled density arrays
        lock (densityPoolLock)
        {
            foreach (var pool in densityPoolByLod.Values)
            {
                while (pool.Count > 0)
                {
                    var a = pool.Pop();
                    if (a.IsCreated) a.Dispose();
                }
            }
        }

        // Clear pending queues
        while (completedNoiseQueue.TryDequeue(out _)) { }
        meshApplyQueue.Clear();
        generatingSet.Clear();
        activeNoiseTasks.Clear();

        // Destroy pooled chunk gameobjects
        while (chunkGoPool.Count > 0)
        {
            var go = chunkGoPool.Pop();
            if (go != null)
                DestroyImmediate(go);
        }

        // Dispose all full-res block arrays that were never cleaned
        foreach (var kv in fullResBlocksByChunk)
        {
            try
            {
                if (kv.Value.IsCreated)
                    kv.Value.Dispose();
            }
            catch { /* already disposed, ignore */ }
        }
        fullResBlocksByChunk.Clear();

        // Also clear leftover pending writes
        pendingWritesByChunk.Clear();
        chunksNeedingRemesh.Clear();

        // Dispose block database
        if (nativeBlockDatabase.IsCreated)
            nativeBlockDatabase.Dispose();

        // Dispose compute detector
        openFaceDetector?.Dispose();
    }

}
