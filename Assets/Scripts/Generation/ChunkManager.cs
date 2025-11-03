using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static GreedyMeshJob;

public class ChunkManager : MonoBehaviour
{
    // =============================
    // === WORLD CONFIGURATION ====
    // =============================

    [Header("World")]
    public GameObject player;
    [SerializeField] private BlockDatabase blockDatabase;
    public int viewDistance = 3;
    public int maxDistanceBeforeUnload = 3; // Chunk unload fallback - Chunk unloading is mostly determined by max chunks - culls chunks in fringe cases and handles frontier
    [SerializeField] private RenderShape renderShape = RenderShape.Sphere;
    public int chunkSize = 32;              // Logical voxel count per axis (mesh uses chunkSize)
    [SerializeField] int maxChunks;         // Max chunks loaded before most distant chunks begin to unload
    [SerializeField] int chunkUnloadBuffer; // How many more chunks than max before chunk unload begins - prevents chunk flicker

    // Derived values
    int densityCount => (chunkSize + 1) * (chunkSize + 1) * (chunkSize + 1); // using +1 border pattern

    // References
    NoiseGenerator noiseGen;

    // =============================
    // === PERFORMANCE TUNING =====
    // =============================

    [Header("Performance tuning")]
    public int maxConcurrentNoiseTasks = 4;   // How many noise tasks to allow concurrently - Increase until CPU saturates or frame drops.
    public int maxMeshUploadsPerFrame = 2;    // How many mesh uploads (Mesh.SetVertices/SetTriangles) per frame - Increase until GPU upload hitching appears.
    public int minConcurrentNoiseTasks = 2;
    public int minMeshUploadsPerFrame = 2;
    public float scheduleInterval = 1f;       // Seconds between scheduling passes - Lower until chunk popping delay is acceptable.                                            

    [Header("Pre-allocation tuning")]
    // Configurable pool sizes (tweakable)
    [SerializeField] int prewarmDensity = 8;  // Initial pooled native arrays - Increase if allocations appear in Profiler. Reduce if memory stays flat but half the pool never gets used.
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
    public int CurrentPoolSize => densityPool.Count();

    // =============================
    // === DATA STRUCTURES / STATE =
    // =============================

    // Internal chunk tracking
    readonly Dictionary<int3, Chunk> chunks = new();
    readonly HashSet<int3> generating = new();   // coords reserved for generation or meshing

    // Threading / queues
    readonly ConcurrentQueue<(int3 coord, float[] density)> completedNoise = new();
    SemaphoreSlim noiseLimiter = null;
    readonly HashSet<int3> activeNoiseTasks = new();

    // Job handles and pending work
    private readonly List<(int3 coord, JobHandle handle, NativeArray<float> density, NativeArray<byte> blockIds)> pendingBlockJobs = new(); // Waiting for block ID generation
    private readonly List<(JobHandle handle, MeshData meshData, NativeArray<byte> blockIds)> pendingMeshJobs = new(); // Waiting for mesh completion
    readonly Queue<MeshData> meshQueue = new();  // Meshes ready to apply on main thread

    // Tokens
    CancellationTokenSource cts;

    // =============================
    // === FLOOD-FILL SYSTEM =======
    // =============================

    readonly Queue<int3> frontier = new();                    // chunks waiting to be expanded
    //readonly Dictionary<int3, OpenFaces> openFaceMap = new();  // what faces were open for each coord
    readonly HashSet<int3> deferredFrontier = new();           // neighbors to expand later (out of range)
    private int3 lastPlayerChunk = int3.zero;
    private bool firstFloodTriggered = false;

    // Debug visualization accessors
    //public IReadOnlyDictionary<int3, OpenFaces> GetOpenFaceMap() => openFaceMap;
    public HashSet<int3> GetDeferredFrontier() => deferredFrontier;

    // =============================
    // === RESOURCES / MATERIALS ===
    // =============================
    [Header("Resources Materials")]
    private NativeArray<BlockDatabase.BlockInfoUnmanaged> nativeBlockDatabase;
    [SerializeField] public BlockTextureArrayBuilder textureArrayBuilder;
    [SerializeField] public Material voxelMaterial;
    private OpenFaceDetector faceDetector;  // kernel

    // =============================
    // === POOLING SYSTEM =========
    // =============================

    readonly Stack<NativeArray<float>> densityPool = new();
    readonly Stack<GameObject> chunkGoPool = new();
    public Stack<Mesh> meshPool = new();

    // =============================
    // === TIMING =================
    // =============================

    float lastScheduleTime = -999f;

    // TODO: Consider adding explanation on how scheduling interacts with async job flow and prewarming logic

    void Awake()
    {
        cts = new CancellationTokenSource();
        // Throttle the number of concurrent noise tasks:
        noiseLimiter = new SemaphoreSlim(Math.Max(1, maxConcurrentNoiseTasks), Math.Max(1, maxConcurrentNoiseTasks));
        // create noise generator
        noiseGen = new NoiseGenerator(111); // adjust constructor if needed
        // create blockDatabase Native
        nativeBlockDatabase = blockDatabase.ToNative(Allocator.Persistent);
        // load texture array
        voxelMaterial.SetTexture("_Textures", textureArrayBuilder.GetTextureArray());
        // create openfacedetector (Can be moved to a computeShaderManager when we are using more)
        faceDetector = new OpenFaceDetector(ref nativeBlockDatabase);
        PrewarmPools();
    }

    void Start()
    {
        // Seed frontier at the player's current chunk
        var playerPos = player.transform.position;
        int3 playerChunk = WorldToChunkCoord(playerPos);
        //frontier.Enqueue(playerChunk);
        ReSeedFloodFrontier(playerChunk, 3);
    }

    void Update()
    {
        // PROFILER: live stat updates each frame
        ChunkProfiler.ReportNoiseCount(maxConcurrentNoiseTasks - noiseLimiter.CurrentCount);
        ChunkProfiler.ReportUploadQueue(meshQueue.Count + generating.Count);
        ChunkProfiler.ReportChunkCount(chunks.Count);
        ChunkProfiler.ReportMeshCount(pendingMeshJobs.Count);

        // Periodic scheduling (throttled)
        if (Time.time - lastScheduleTime >= scheduleInterval)
        {
            lastScheduleTime = Time.time;

            if (frontier.Count > 0) 
                ScheduleVisibleChunksFloodFill();
        }

        // --- Flood-fill activation when entering new chunk ---
        int3 currentPlayerChunk = WorldToChunkCoord(player.transform.position);

        // Trigger flood-fill expansion whenever the player enters a new chunk
        if (!firstFloodTriggered || !currentPlayerChunk.Equals(lastPlayerChunk))
        {
            lastPlayerChunk = currentPlayerChunk;
            firstFloodTriggered = true;
            // Re-prioritize work for the new center
            PromoteDeferredToFrontier(currentPlayerChunk);
            // Kick a scheduling tick immediately so promoted items start right away
            ScheduleVisibleChunksFloodFill();
        }
        // Manage completed noise tasks, schedules block assignment
        while (completedNoise.TryDequeue(out var data))
        {
            // pull an array from pool
            NativeArray<float> nativeDensity;
            lock (densityPool)
            {
                nativeDensity = densityPool.Count > 0
                    ? densityPool.Pop()
                    : new NativeArray<float>(densityCount, Allocator.Persistent);
            }

            nativeDensity.CopyFrom(data.density);

            // schedule block assignment async and track it
            var (handle, blockIds) = CreateBlockAssignmentJob(nativeDensity, data.coord);
            pendingBlockJobs.Add((data.coord, handle, nativeDensity, blockIds));

            // allow scheduling that coord again later
            lock (activeNoiseTasks) activeNoiseTasks.Remove(data.coord);

        }

        // Check block assignment jobs
        for (int i = pendingBlockJobs.Count - 1; i >= 0; i--)
        {
            var it = pendingBlockJobs[i];
            if (!it.handle.IsCompleted) continue;

            it.handle.Complete();

            // Testing chunk flood fill system with compute shaders and other approaches
            //var sw = System.Diagnostics.Stopwatch.StartNew();             // Stopwatch      
            //var faces = DetectOpenFacesFromBlocks(it.blockIds);           // CPU VERSION
            var faces = DetectTerrainFlowFromBlocks(it.blockIds);           // CPU VERSION Flow Fill system
            //var faces = faceDetector.DetectGPU(it.blockIds, chunkSize);   // GPU VERSION

            //openFaceMap[it.coord] = faces;
            //sw.Stop();
            //Debug.Log($"GPU FaceScan: {sw.Elapsed.TotalMilliseconds:F3}ms");

            ExpandFrontierFromFaces(it.coord, faces);

            // Mesh scheduling now uses blockIds
            ScheduleGreedyMeshJob(it.coord, it.blockIds);

            // Return density to pool
            if (it.density.IsCreated)
            {
                lock (densityPool)
                    if (densityPool.Count < prewarmDensity)
                        densityPool.Push(it.density);
                    else
                        it.density.Dispose();
            }

            pendingBlockJobs.RemoveAt(i);
        }

        for (int i = pendingMeshJobs.Count - 1; i >= 0; i--)
        {
            var entry = pendingMeshJobs[i];
            if (!entry.handle.IsCompleted) continue;

            entry.handle.Complete();

            if (entry.blockIds.IsCreated) entry.blockIds.Dispose(); // << dispose blockIds

            meshQueue.Enqueue(entry.meshData);
            pendingMeshJobs.RemoveAt(i);
        }

        // Apply a limited number of meshes to Unity per frame
        int uploads = 0;
        while (uploads < maxMeshUploadsPerFrame && meshQueue.Count > 0)
        {
            var meshData = meshQueue.Dequeue();
            ApplyMeshDataToChunk(meshData);
            uploads++;
        }

        if (Time.frameCount % 30 == 0) UnloadChunks(currentPlayerChunk);

        AdaptivePerformanceControl();
    }

    // flood-fill scheduler (closest-first implicitly due to frontier expansion pattern)
    void ScheduleVisibleChunksFloodFill()
    {
        if (player == null || frontier.Count == 0) return;

        int spawnedThisTick = 0;
        int cap = Math.Max(1, maxConcurrentNoiseTasks);

        // pull up to 'cap' coords from frontier, respecting range/existence
        while (frontier.Count > 0 && spawnedThisTick < cap)
        {
            var coord = frontier.Dequeue();

            if (chunks.ContainsKey(coord) || generating.Contains(coord))
                continue; // already have / in-flight

            generating.Add(coord);
            spawnedThisTick++;
            ScheduleNoiseTask(coord);
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

            if (chunks.ContainsKey(n) || generating.Contains(n))
                continue;

            if (IsChunkWithinRenderRange(n, center, viewDistance))
            {
                if (!frontier.Contains(n))
                    frontier.Enqueue(n);
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
            if (chunks.ContainsKey(c) || generating.Contains(c))
                continue;
     
            if (IsChunkWithinRenderRange(c, center, viewDistance))
                toPromote.Add(c);
        }

        foreach (var c in toPromote)
        {
            frontier.Enqueue(c);
            deferredFrontier.Remove(c);
        }
    }

    void ScheduleNoiseTask(int3 coord)
    {
        lock (activeNoiseTasks)
        {
            if (activeNoiseTasks.Contains(coord)) return;
            activeNoiseTasks.Add(coord);
        }

        // Report current task count
        ChunkProfiler.ReportNoiseCount(activeNoiseTasks.Count);

        Task.Run(async () =>
        {
            bool acquired = false;

            try
            {
                await noiseLimiter.WaitAsync(cts.Token);
                acquired = true;
                if (cts.IsCancellationRequested) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Generate the noise — runs on worker thread
                float[] density = noiseGen.FillDensity(coord, chunkSize, densityCount);

                sw.Stop();

                // Enqueue for mesh generation (thread-safe)
                completedNoise.Enqueue((coord, density));

                // Record duration for profiling
                lock (ChunkProfilerTimes.noiseLock)
                    ChunkProfilerTimes.noiseDurations.Enqueue((float)sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down — safe to ignore
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Noise task failed for {coord}: {ex}");
            }
            finally
            {
                if (acquired)
                    noiseLimiter.Release(); // Only release if we actually acquired

                lock (activeNoiseTasks)
                    activeNoiseTasks.Remove(coord);
            }
        });
    }


    private (JobHandle handle, NativeArray<byte> blockIds) CreateBlockAssignmentJob(NativeArray<float> nativeDensity, int3 coord)
    {
        int voxelCount = (chunkSize + 1) * (chunkSize + 1) * (chunkSize + 1);
        var blockIds = new NativeArray<byte>(voxelCount, Allocator.Persistent);

        var job = new BlockAssignmentJob
        {
            density = nativeDensity,
            blockIds = blockIds,
            chunkSize = chunkSize,
            startingCoord = coord,
            voxelCount = voxelCount,
            //solidBlockId = 1, // change if you want (e.g. Stone)
            //airBlockId = 0 // Air
        };

        var handle = job.Schedule(voxelCount, 64); // async, no Complete()
        return (handle, blockIds);
    }

    // face scan on +1 density buffer
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

    // face scan on +1 density buffer
    private OpenFaces DetectTerrainFlowFromBlocks(NativeArray<byte> blockIds)
    {
        OpenFaces flags = OpenFaces.None;
        int s = chunkSize + 1;

        bool IsSolid(int x, int y, int z) =>
            blockIds[x + y * s + z * s * s] != 0; // non-air = solid

        bool FaceExposed(Func<int, int, bool> test)
        {
            bool sawSolid = false;
            bool sawAir = false;

            for (int i = 0; i < s; i++)
            {
                for (int j = 0; j < s; j++)
                {
                    bool solid = test(i, j);
                    if (solid) sawSolid = true; else sawAir = true;

                    if (sawSolid && sawAir) return true; // ? mixed ? exposed
                }
            }
            return false; // fully solid or fully air ? not exposed
        }

        // +X: (s-1, y, z)
        if (FaceExposed((y, z) => IsSolid(s - 1, y, z)))
            flags |= OpenFaces.PosX;

        // -X: (0, y, z)
        if (FaceExposed((y, z) => IsSolid(0, y, z)))
            flags |= OpenFaces.NegX;

        // +Y: (x, s-1, z)
        if (FaceExposed((x, z) => IsSolid(x, s - 1, z)))
            flags |= OpenFaces.PosY;

        // -Y: (x, 0, z)
        if (FaceExposed((x, z) => IsSolid(x, 0, z)))
            flags |= OpenFaces.NegY;

        // +Z: (x, y, s-1)
        if (FaceExposed((x, y) => IsSolid(x, y, s - 1)))
            flags |= OpenFaces.PosZ;

        // -Z: (x, y, 0)
        if (FaceExposed((x, y) => IsSolid(x, y, 0)))
            flags |= OpenFaces.NegZ;

        return flags;
    }

    void ScheduleGreedyMeshJob(int3 coord, NativeArray<byte> blockIds)
    {
        ChunkProfiler.MeshStart(); // Wont track an async job. Add handle.Complete() to profile

        var meshData = new MeshData
        {
            coord = coord,
            vertices = new NativeList<float3>(Allocator.Persistent),
            triangles = new NativeList<int>(Allocator.Persistent),
            normals = new NativeList<float3>(Allocator.Persistent),
            colors = new NativeList<float4>(Allocator.Persistent),
            UV0s = new NativeList<float2>(Allocator.Persistent)
        };

        var job = new GreedyMeshJob
        {
            blockArray = blockIds,
            blocks = nativeBlockDatabase,
            chunkSize = chunkSize,
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

        if (chunk == null)
        {
            if (!chunkEmpty)
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
                chunk.chunkMaterial = voxelMaterial;

                chunks[meshData.coord] = chunk;
            }
            else
            {
                //chunks[meshData.coord] = null;
            }
        }

        if (chunk != null)
            chunk.ApplyMesh(meshData, meshPool);

        DisposeMeshData(ref meshData);
        generating.Remove(meshData.coord);

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

        lock (densityPool)
        {
            if (densityPool.Count < prewarmDensity)
            {
                var extra = new NativeArray<float>(densityCount, Allocator.Persistent);
                densityPool.Push(extra);
                //prewarmDensity++;
                adjusted = true;
            }
        }

        if (adjusted)
        {
            //Debug.Log($"[PerfTuner] {avgFrameTime:F1}ms ? Noise={maxConcurrentNoiseTasks}, Uploads={maxMeshUploadsPerFrame}, Interval={scheduleInterval:F2}");
        }
    }

    // Create an initial zone around the player from which the flood fill can start
    void ReSeedFloodFrontier(int3 centerChunk, short radius)
    {
        float sqrViewDist = radius * radius;

        for (int x = -radius; x <= radius; x++)
            for (int y = -radius; y <= radius; y++)
                for (int z = -radius; z <= radius; z++)
                {
                    float distSqr = x * x + y * y + z * z;
                    if (distSqr > sqrViewDist)
                        continue;

                    int3 coord = centerChunk + new int3(x, y, z);

                    // skip if chunk already exists or is generating
                    if (chunks.ContainsKey(coord) || generating.Contains(coord))
                        continue;

                    // skip if already in frontier
                    if (frontier.Contains(coord))
                        continue;

                    frontier.Enqueue(coord);
                }

        // Immediately run a flood-fill tick (respects throttling)
        ScheduleVisibleChunksFloodFill();
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
                if (GetChunkDistanceWithRenderShape(coord, currentPlayerChunk) > maxDistanceBeforeUnload + 1)
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
                    if (GetChunkDistanceWithRenderShape(coord, currentPlayerChunk) > maxDistanceBeforeUnload)
                        chunksToRemove.Add(coord);
                }
                foreach (int3 coord in chunksToRemove)
                    ReleaseChunk(coord);
            }
            return; 
        }

        var playerPos = player.transform.position;

        // Sort by distance to player
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

    // =============================
    // ===== HELPER FUNCTIONS ======
    // =============================
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

        for (int i = 0; i < prewarmDensity; i++)
            densityPool.Push(new NativeArray<float>(densityCount, Allocator.Persistent));
    }
    public bool IsChunkWithinRenderRange(int3 chunkCoord, int3 playerChunk, float viewDistance)
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
                    float2 flatchunk1 = chunk1.xz;
                    float2 flatChunk2 = chunk2.xz;
                    float dist2D = math.distance(flatchunk1, flatChunk2);
                    float verticalDelta = math.abs(chunk1.y - chunk2.y);

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
        foreach (var (coord, handle, density, blockIds) in pendingBlockJobs.ToArray())
        {
            handle.Complete();
            if (density.IsCreated) density.Dispose();
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
        lock (densityPool)
        {
            while (densityPool.Count > 0)
            {
                var a = densityPool.Pop();
                if (a.IsCreated) a.Dispose();
            }
        }

        // Clear pending queues
        while (completedNoise.TryDequeue(out _)) { }
        meshQueue.Clear();
        generating.Clear();
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
        faceDetector?.Dispose();
    }
}
