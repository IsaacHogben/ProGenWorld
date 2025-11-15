// ChunkManager.cs
// High-level orchestration for chunk generation, LOD updates, and async job scheduling.
// This file manages chunk lifecycle, noise tasks, block assignment jobs, mesh jobs,
// pooling, and flood-fill terrain expansion.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static GreedyMeshJob;

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
    public int unloadDistance = 3;
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

    // Threading / queues
    readonly ConcurrentQueue<(int3 coord, NativeArray<float> density, LODLevel lod)> completedNoiseQueue = new();
    SemaphoreSlim noiseLimiter = null;
    readonly HashSet<int3> activeNoiseTasks = new();

    // Job handles and pending work
    private readonly List<(int3 coord, JobHandle handle, NativeArray<float> density, NativeArray<byte> blockIds, LODLevel lod)> pendingBlockJobs = new(); // Waiting for block ID generation
    private readonly List<(JobHandle handle, MeshData meshData, NativeArray<byte> blockIds)> pendingMeshJobs = new(); // Waiting for mesh completion
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
    private int3 lastPlayerChunk = int3.zero;
    private bool firstFloodTriggered = false;
    public HashSet<int3> GetDeferredFrontier() => deferredFrontier;

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
    // Timing
    // --------------------------------------------------
    float lastScheduleTime = -999f;


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
        lodConfigs[LODLevel.Near] = new LODSettings { stride = 1, detailedBlocks = true, material = voxelMaterial};
        lodConfigs[LODLevel.Mid] = new LODSettings { stride = 2, detailedBlocks = false, material = midMaterial};
        lodConfigs[LODLevel.Far] = new LODSettings { stride = 4, detailedBlocks = false, material = farMaterial};
        // Compute density counts per LOD
        foreach (var kv in lodConfigs.ToArray())
        {
            var lod = kv.Key;
            var cfg = kv.Value;

            int lodChunkSize = chunkSize / cfg.stride;
            cfg.densityCount = (lodChunkSize + 1) * (lodChunkSize + 1) * (lodChunkSize + 1);

            lodConfigs[lod] = cfg;
        }
        InitDensityPools();
        PrewarmPools(); 
    }

    void Start()
    {
        // Seed frontier at the player's current chunk
        InitFloodFrontier(WorldToChunkCoord(player.transform.position), 0);
    }

    void Update()
    {
        // PROFILER: live stat updates each frame
        ChunkProfiler.ReportNoiseCount(maxConcurrentNoiseTasks - noiseLimiter.CurrentCount);
        ChunkProfiler.ReportUploadQueue(meshApplyQueue.Count + generatingSet.Count);
        ChunkProfiler.ReportChunkCount(chunks.Count);
        ChunkProfiler.ReportMeshCount(pendingMeshJobs.Count);

        // Periodic scheduling (throttled)
        if (Time.time - lastScheduleTime >= scheduleInterval)
        {
            lastScheduleTime = Time.time;

            if (frontierQueue.Count > 0) 
                ScheduleNoiseTask();
        }

        // --- Flood-fill activation when entering new chunk ---
        int3 currentPlayerChunk = WorldToChunkCoord(player.transform.position);

        // Trigger flood-fill expansion whenever the player enters a new chunk
        if (!firstFloodTriggered || !currentPlayerChunk.Equals(lastPlayerChunk))
        {
            lastPlayerChunk = currentPlayerChunk;
            firstFloodTriggered = true;
            OnPlayerMovedChunks(currentPlayerChunk);
        }
        // Manage completed noise tasks, schedules block assignment
        while (completedNoiseQueue.TryDequeue(out var data))
        {
            var (handle, blockIds) = CreateBlockAssignmentJob(
                data.density,
                data.coord,
                lodConfigs[data.lod].stride);

            pendingBlockJobs.Add((data.coord, handle, data.density, blockIds, data.lod));
        }

        // Check block assignment jobs
        for (int i = pendingBlockJobs.Count - 1; i >= 0; i--)
        {
            var it = pendingBlockJobs[i];
            if (!it.handle.IsCompleted) continue;

            it.handle.Complete();

            var faces = DetectTerrainFlowFromBlocks(it.blockIds, it.lod);
            ExpandFrontierFromFaces(it.coord, faces);
            ScheduleMeshJob(it.coord, it.blockIds, it.lod);

            // Return density to pool
            if (it.density.IsCreated)
                ReturnDensityArray(it.lod, it.density);

            pendingBlockJobs.RemoveAt(i);
        }

        for (int i = pendingMeshJobs.Count - 1; i >= 0; i--)
        {
            var entry = pendingMeshJobs[i];
            if (!entry.handle.IsCompleted) continue;

            entry.handle.Complete();

            if (entry.blockIds.IsCreated) entry.blockIds.Dispose(); // << dispose blockIds

            meshApplyQueue.Enqueue(entry.meshData);
            pendingMeshJobs.RemoveAt(i);
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
    }

    private void OnPlayerMovedChunks(int3 currentPlayerChunk)
    {
        PromoteDeferredToFrontier(currentPlayerChunk);
        CullOutOfRangeBlockJobs(currentPlayerChunk);
        UpdateChunkLods(currentPlayerChunk);
        UnloadChunks(currentPlayerChunk);
    }
    void UpdateChunkLods(int3 playerChunk)
    {
        // priority queue buckets: small to large stride means higher priority
        List<int3> toPromoteNear = new(32);  // highest priority (stride 2 1)
        List<int3> toPromoteMid = new(32);  // mid priority (stride 4 2)
        //List<int3> toPromote3 = new(32);  // lowest

        foreach (var kv in chunks)
        {
            var coord = kv.Key;
            var chunk = kv.Value;
            if (chunk == null) continue;

            //var lod = ;
            var desiredLod = GetLODForCoord(coord, playerChunk);
            var chunkLod = chunk.lod;
            if (desiredLod == chunkLod)
                continue;

            // Promote only (never lower LOD) ----------- Change to here if we want LOD's to downgrade as well
            if (desiredLod < chunkLod)
            {
                if (chunkLod == LODLevel.Mid) EnqueueLodUpdate(kv.Key, LODLevel.Near); //toPromoteNear.Add(kv.Key);
                else if (chunkLod == LODLevel.Far) EnqueueLodUpdate(kv.Key, LODLevel.Mid);
                //else if (cs == 8) toPromote3.Add(kv.Key);
            }
        }
    }

    // flood-fill scheduler (closest-first implicitly due to frontier expansion pattern)
    void ScheduleNoiseTask()
    {
        if (player == null) return;

        int spawnedThisTick = 0;
        int cap = Math.Max(1, maxConcurrentNoiseTasks);


        // Here we are initiating a number of tasks that will eventually result in chunks but, we are limiting this per tick and prioritizing LOD upgrades over new chunk generation.
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
                LODLevel lod = GetLODForCoord(coord, WorldToChunkCoord(player.transform.position));
                StartNoiseTask(coord, lod);
            }
    }

    void ExpandFrontierFromFaces(int3 coord, OpenFaces faces)
    {
        if (faces == OpenFaces.None) return;

        var center = WorldToChunkCoord(player.transform.position);

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

            if (IsChunkWithinRange(n, center, viewDistance))
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
    void PromoteDeferredToFrontier(int3 center)
    {
        // promote any deferred chunks now within view distance
        List<int3> toPromote = new(32);

        foreach (var c in deferredFrontier)
        {
            if (chunks.ContainsKey(c) || generatingSet.Contains(c))
                continue;
     
            if (IsChunkWithinRange(c, center, viewDistance))
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

                densityBuffer.CopyFrom(noiseGenerator.FillDensity(coord, chunkSize, frequency, lodSettings.stride)); //Editing native array in asyn task is unsafe. Is currently accounted for.
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

    private (JobHandle handle, NativeArray<byte> blockIds) CreateBlockAssignmentJob(NativeArray<float> nativeDensity, int3 coord, int stride)
    {
        int chunkLodSize = chunkSize / stride;
        int voxelCount = (chunkLodSize + 1) * (chunkLodSize + 1) * (chunkLodSize + 1);
        var blockIds = new NativeArray<byte>(voxelCount, Allocator.Persistent);

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
        // determine stride based on LOD
        int stride = lod switch
        {
            LODLevel.Near => 1,
            LODLevel.Mid => 2,
            LODLevel.Far => 4,
            _ => 1
        };

        int s = (chunkSize / stride) + 1;
        OpenFaces flags = OpenFaces.None;

        bool IsSolid(int x, int y, int z)
            => blockIds[x + y * s + z * s * s] != 0; // non-air = solid

        bool FaceExposed(Func<int, int, bool> test)
        {
            bool sawSolid = false;
            bool sawAir = false;

            // sample every Nth voxel depending on LOD stride
            for (int i = 0; i < s; i += stride)
            {
                for (int j = 0; j < s; j += stride)
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
    void ScheduleMeshJob(int3 coord, NativeArray<byte> blockIds, LODLevel lod)
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
            stride = lodConfigs[lod].stride,
            lod = lod
        };

        var job = new GreedyMeshJob
        {
            blockArray = blockIds,
            blocks = nativeBlockDatabase,
            chunkSize = chunkSize / lodConfigs[lod].stride,
            meshData = meshData,
        };

        JobHandle handle = job.Schedule();
        pendingMeshJobs.Add((handle, meshData, blockIds));
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

        DisposeMeshData(ref meshData);
        generatingSet.Remove(meshData.coord);
        deferredFrontier.Remove(meshData.coord); /// sus?

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
    void InitFloodFrontier(int3 centerChunk, short radius) // Create the initial position for flood fill to begin. Is the players location at start of world load.
    {
        float sqrViewDist = radius * radius;

        for (int x = -radius; x <= radius; x++)
            for (int y = -5; y <= 5; y++)
                for (int z = -radius; z <= radius; z++)
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
        ScheduleNoiseTask();
    }
    
    // Unloads chunks when the number of chunks in play exeeds the maxChunks value. Unloads based on furtheset distance from player
    void UnloadChunks(int3 currentPlayerChunk)
    {
        if (chunks.Count < maxChunks + chunkUnloadBuffer) 
        {
            bool chunksExistAtMaxDistance = false;

            List<int3> defferedFrontierToRemove = new(32);
            foreach (int3 coord in deferredFrontier)
            {
                if (GetChunkDistanceWithRenderShape(coord, currentPlayerChunk) > unloadDistance + 1)
                    defferedFrontierToRemove.Add(coord);
                chunksExistAtMaxDistance = true;
            }
            foreach (int3 coord in defferedFrontierToRemove)
                deferredFrontier.Remove(coord);

            if (chunksExistAtMaxDistance)
            {   
                List<int3> chunksToRemove = new(32);
                foreach (var kvp in chunks)
                {
                    var coord = kvp.Key;
                    if (GetChunkDistanceWithRenderShape(coord, currentPlayerChunk) > unloadDistance)
                        chunksToRemove.Add(coord);
                }
                foreach (int3 coord in chunksToRemove)
                    ReleaseChunk(coord);
            }
            return; 
        }

        if (unloadAtMaxChunks)
        {// Sort by distance to player
        var sortedChunks = chunks
            .OrderBy(kv => -GetChunkDistanceWithRenderShape(kv.Key, currentPlayerChunk)) // Sort by furtheset from player
            .ToList();

            foreach (var kvp in sortedChunks)
            {
                var coord = kvp.Key;
                var chunk = kvp.Value;
                if (chunk == null) continue;

                ReleaseChunk(coord);

                if (chunks.Count < maxChunks)
                    return;
            }
        }
    }

    // =============================
    // ===== HELPER FUNCTIONS ======
    // =============================
    
    LODLevel GetLODForCoord(int3 coord, int3 playerChunk)
    {
        float dist = GetChunkDistanceWithRenderShape(coord, playerChunk);

        if (dist <= nearRange) return LODLevel.Near;
        if (dist <= midRange) return LODLevel.Mid;
        return LODLevel.Far;
    }
    void EnqueueFrontier(int3 c)
    {
        if (frontierSet.Add(c)) frontierQueue.Enqueue(c);
    }
    bool TryDequeueFrontier(out int3 c)
    {
        while (frontierQueue.Count > 0)
        {
            c = frontierQueue.Dequeue();
            if (frontierSet.Remove(c)) return true; // real item
        }
        c = default; return false;
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
    void CullOutOfRangeBlockJobs(int3 center)
    {
        for (int i = pendingBlockJobs.Count - 1; i >= 0; --i)
        {
            var it = pendingBlockJobs[i];
            if (!IsChunkWithinRange(it.coord, center, viewDistance + 1)) // small buffer
            {
                it.handle.Complete();
                if (it.density.IsCreated) it.density.Dispose();
                if (it.blockIds.IsCreated) it.blockIds.Dispose();
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
    }
    static void DisposeMeshData(ref MeshData m)
    {
        if (m.vertices.IsCreated) m.vertices.Dispose();
        if (m.triangles.IsCreated) m.triangles.Dispose();
        if (m.normals.IsCreated) m.normals.Dispose();
        if (m.colors.IsCreated) m.colors.Dispose();
        if (m.UV0s.IsCreated) m.UV0s.Dispose();
    }   
    public Vector3 ChunkToWorld(int3 coord) =>
        new Vector3(coord.x, coord.y, coord.z) * chunkSize; // Public for debug visualizers
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
                return arr;
            }
        }

        // No suitable array in pool, allocate new
        return new NativeArray<float>(requiredLength, Allocator.Persistent);
    }
    void ReturnDensityArray(LODLevel lod, NativeArray<float> arr)
    {
        if (!arr.IsCreated) return;

        var cfg = lodConfigs[lod];
        if (arr.Length != cfg.densityCount)
        {
            // Wrong size – just dispose
            arr.Dispose();
            return;
        }

        lock (densityPoolLock)
        {
            var pool = densityPoolByLod[lod];
            if (pool.Count >= maxDensityPoolPerLod)
            {
                arr.Dispose();
                return;
            }

            pool.Push(arr);
        }
    }
    public bool IsChunkWithinRange(int3 chunkCoord, int3 playerChunk, float viewDistance)
    {
        return GetChunkDistanceWithRenderShape(chunkCoord, playerChunk) <= viewDistance;
    }
    float GetChunkDistanceWithRenderShape(int3 chunk1, int3 chunk2)
    {
        switch (renderShape)
        {
            case RenderShape.Cylinder:
                {
                    // 2D distance (XZ only)
                    int2 flatchunk1 = chunk1.xz;
                    int2 flatChunk2 = chunk2.xz;
                    float dist2D = math.distance(flatchunk1, flatChunk2);

                    // Allow full vertical range, only check XZ radius
                    return dist2D;
                }

            case RenderShape.Sphere:
            default:
                {
                    float dist3D = math.distance(chunk1, chunk2);
                    return dist3D;
                }
        }
    }
    void CompleteAllJobs()
    {
        foreach (var (coord, handle, density, blockIds, lod) in pendingBlockJobs.ToArray())
        {
            handle.Complete();
            if (density.IsCreated) ReturnDensityArray(lod, density);
            if (blockIds.IsCreated) blockIds.Dispose();
        }
        pendingBlockJobs.Clear();

        // Complete and dispose pending jobs safely
        for (int i = pendingMeshJobs.Count - 1; i >= 0; i--)
        {
            var (handle, meshData, blockIds) = pendingMeshJobs[i];
            if (!handle.IsCompleted) handle.Complete();

            // Dispose blockIds — no density arrays here anymore
            if (blockIds.IsCreated)
            {
                blockIds.Dispose();
            }

            // Dispose mesh native lists (if not already disposed by ApplyMesh)
            if (meshData.vertices.IsCreated) meshData.vertices.Dispose();
            if (meshData.triangles.IsCreated) meshData.triangles.Dispose();
            if (meshData.normals.IsCreated) meshData.normals.Dispose();
            if (meshData.colors.IsCreated) meshData.colors.Dispose();
            if (meshData.UV0s.IsCreated) meshData.UV0s.Dispose();

            pendingMeshJobs.RemoveAt(i);
        }
        pendingMeshJobs.Clear();
    }
    void OnDestroy()
    {
        CompleteAllJobs();
        cts?.Cancel(); // Cancels async task that was not able to be jobified (NoiseGen, )
        cts?.Dispose();
        // Dispose any leftover pooled density arrays
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

        // Optionally destroy pooled gameobjects
        while (chunkGoPool.Count > 0)
        {
            var go = chunkGoPool.Pop();
            if (go != null) DestroyImmediate(go);
        }

        // Remaining Clean-up
        if (nativeBlockDatabase.IsCreated)
            nativeBlockDatabase.Dispose();
        openFaceDetector?.Dispose();
    }
}
