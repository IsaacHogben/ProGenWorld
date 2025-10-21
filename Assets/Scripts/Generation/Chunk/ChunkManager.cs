using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static GreedyMeshJob;

public class ChunkManager : MonoBehaviour
{
    [Header("World")]
    public int viewDistance = 3;
    public int chunkSize = 32;                  // Logical voxel count per axis (mesh uses chunkSize)
    public float isoLevel = 0f;
    public Material chunkMaterial;
    public GameObject player;

    [Header("Performance tuning")]
    public int maxConcurrentNoiseTasks = 4;     // How many noise tasks to allow concurrently - Increase until CPU saturates or frame drops.
    public int maxMeshUploadsPerFrame = 2;      // How many mesh uploads (Mesh.SetVertices/SetTriangles) per frame - Increase until GPU upload hitching appears.
    public float scheduleInterval = 0.1f;       // Seconds between scheduling passes - Lower until chunk popping delay is acceptable.
    public int initialPoolSize = 8;             // Initial pooled native arrays - Increase initialPoolSize if allocations appear in Profiler. Reduce if memory stays flat but half the pool never gets used.

    // --- Performance diagnostics ---
    public float AvgFrameTime => avgFrameTime;
    public int CurrentNoiseTasks => maxConcurrentNoiseTasks;
    public int CurrentMeshUploads => maxMeshUploadsPerFrame;
    public float CurrentScheduleInterval => scheduleInterval;
    public int CurrentPoolSize => initialPoolSize;

    // Internal state
    readonly Dictionary<int3, Chunk> chunks = new();
    readonly HashSet<int3> emptyChunks = new();
    readonly HashSet<int3> generating = new(); // coords reserved for generation or meshing

    // Threading/queues
    readonly ConcurrentQueue<(int3 coord, float[] density)> completedNoise = new();
    SemaphoreSlim noiseLimiter = null;
    readonly HashSet<int3> activeNoiseTasks = new();

    // NativeArray pool for densities (Allocator.Persistent)
    readonly Stack<NativeArray<float>> densityPool = new();

    // Pending mesh jobs: handle -> meshData -> associated density (from pool) & mask (if used)
    List<(JobHandle handle, MeshData meshData, NativeArray<float> density, NativeArray<FMask> mask)> pendingJobs = new();

    // Mesh queue to apply on main thread
    readonly Queue<MeshData> meshQueue = new();

    // Chunk GameObject pool
    readonly Stack<GameObject> chunkGoPool = new();

    // Timing
    float lastScheduleTime = -999f;

    // References
    NoiseGenerator noiseGen;

    // Derived
    int densityCount => (chunkSize + 1) * (chunkSize + 1) * (chunkSize + 1); // using +1 border pattern

    // ==== NEW: flood-fill state ====
    readonly Queue<int3> frontier = new();                          // chunks waiting to be expanded
    readonly Dictionary<int3, OpenFaces> openFaceMap = new();       // what faces were open for each coord
    readonly HashSet<int3> deferredFrontier = new();                // neighbors we wanted to expand to, but were out of range
    // Flood fill test
    private int3 lastPlayerChunk = int3.zero;
    private bool firstFloodTriggered = false;
    // Expose for debug visualizers
    public IReadOnlyDictionary<int3, OpenFaces> GetOpenFaceMap() => openFaceMap;
    public HashSet<int3> GetDeferredFrontier() => deferredFrontier;


    // --- Adaptive performance control ---
    float avgFrameTime = 16.6f;
    float lastPerfAdjustTime = 0f;


    void Awake()
    {
        // Throttle the number of concurrent noise tasks:
        noiseLimiter = new SemaphoreSlim(Math.Max(1, maxConcurrentNoiseTasks), Math.Max(1, maxConcurrentNoiseTasks));

        // create noise generator
        noiseGen = new NoiseGenerator(111); // adjust constructor if needed

        // prewarm density pool
        for (int i = 0; i < initialPoolSize; i++)
            densityPool.Push(new NativeArray<float>(densityCount, Allocator.Persistent));
    }

    void Start()
    {
        // Seed frontier at the player's current chunk
        var playerPos = player.transform.position;
        int3 playerChunk = WorldToChunkCoord(playerPos);
        frontier.Enqueue(playerChunk);
    }

    void Update()
    {
        // PROFILER: live stat updates each frame
        ChunkProfiler.ReportNoiseCount(maxConcurrentNoiseTasks - noiseLimiter.CurrentCount);
        ChunkProfiler.ReportUploadQueue(meshQueue.Count + generating.Count);
        ChunkProfiler.ReportChunkCount(chunks.Count);
        ChunkProfiler.ReportMeshCount(pendingJobs.Count);

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

        // Consume completed noise tasks (background threads put float[] here)
        while (completedNoise.TryDequeue(out var data))
        {
            // obtain a pooled NativeArray (or allocate if pool empty)
            NativeArray<float> nativeDensity;
            lock (densityPool)
            {
                if (densityPool.Count > 0)
                    nativeDensity = densityPool.Pop();
                else
                    nativeDensity = new NativeArray<float>(densityCount, Allocator.Persistent);
            }

            // copy managed -> native on main thread (fast memcpy)
            nativeDensity.CopyFrom(data.density);

            // detect open faces BEFORE meshing
            var faces = DetectOpenFaces(nativeDensity);
            openFaceMap[data.coord] = faces;

            // immediately expand frontier based on faces
            ExpandFrontierFromFaces(data.coord, faces);

            // schedule greedy mesh job using the pooled nativeDensity; job will produce MeshData
            ScheduleGreedyMeshJob(data.coord, nativeDensity);

            // allow scheduling that coord again in the future if needed
            lock (activeNoiseTasks)
            {
                activeNoiseTasks.Remove(data.coord);
            }
        }

        // Check pending jobs for completion (non-blocking)
        for (int i = pendingJobs.Count - 1; i >= 0; i--)
        {
            var entry = pendingJobs[i];
            if (entry.handle.IsCompleted)
            {
                // Complete and enqueue
                entry.handle.Complete();

                // Dispose mask
                if (entry.mask.IsCreated)
                    entry.mask.Dispose();

                // density we can return to pool now (mesher job already used it)
                lock (densityPool) { densityPool.Push(entry.density); }

                // Enqueue meshdata for main-thread upload; meshData.NativeLists are still valid (allocated TempJob)
                meshQueue.Enqueue(entry.meshData);
                pendingJobs.RemoveAt(i);
            }
        }

        // Apply a limited number of meshes to Unity per frame
        int uploads = 0;
        while (uploads < maxMeshUploadsPerFrame && meshQueue.Count > 0)
        {
            var meshData = meshQueue.Dequeue();
            ApplyMeshDataToChunk(meshData);
            uploads++;
        }

        // Performance adaptation
        AdaptivePerformanceControl();
    }

    // flood-fill scheduler (closest-first implicitly due to frontier expansion pattern)
    void ScheduleVisibleChunksFloodFill()
    {
        if (player == null || frontier.Count == 0) return;

        int3 playerChunk = WorldToChunkCoord(player.transform.position);
        float maxDistSqr = viewDistance * viewDistance;

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
        float maxDistSqr = viewDistance * viewDistance;

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

            float d2 = math.lengthsq((float3)(n - center));
            if (d2 <= maxDistSqr)
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
        float maxDistSqr = viewDistance * viewDistance;

        // promote any deferred chunks now within view distance
        List<int3> toPromote = new(32);

        foreach (var c in deferredFrontier)
        {
            if (chunks.ContainsKey(c) || generating.Contains(c))
                continue;

            float d2 = math.lengthsq((float3)(c - center));
            if (d2 <= maxDistSqr)
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

        // Report count before starting task (main thread safe)
        ChunkProfiler.ReportNoiseCount(activeNoiseTasks.Count);

        // Launch background noise generation
        Task.Run(async () =>
        {
            await noiseLimiter.WaitAsync();

            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();

            try
            {
                // Generate the noise — runs on worker thread
                float[] density = noiseGen.FillDensity(coord, chunkSize, densityCount);

                sw.Stop();

                // Enqueue for mesh generation (thread-safe)
                completedNoise.Enqueue((coord, density));

                // Thread-safe store of result time
                lock (ChunkProfilerTimes.noiseLock)
                {
                    ChunkProfilerTimes.noiseDurations.Enqueue((float)sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Noise task failed for {coord}: {ex}");
            }
            finally
            {
                noiseLimiter.Release();
                lock (activeNoiseTasks)
                {
                    activeNoiseTasks.Remove(coord);
                }
            }
        });
    }

    void ScheduleBlockAssignment(int3 coord, NativeArray<float> density)
    {
        /*// Prepare output array for block IDs
        var blockIds = new NativeArray<byte>(density.Length, Allocator.Persistent);

        // Prepare ranges from the database
        var dbRanges = new NativeArray<BlockAssignmentJob.BlockRange>(
            blockDatabase.blocks.Length, Allocator.TempJob);

        for (int i = 0; i < blockDatabase.blocks.Length; i++)
        {
            var b = blockDatabase.blocks[i];
            dbRanges[i] = new BlockAssignmentJob.BlockRange
            {
                minDensity = b.minDensity,
                maxDensity = b.maxDensity,
                id = b.id
            };
        }

        var job = new BlockAssignmentJob
        {
            density = density,
            blockRanges = dbRanges,
            blockIds = blockIds
        };

        JobHandle handle = job.Schedule(density.Length, 64);
        pendingBlockJobs.Add((coord, handle, density, blockIds, dbRanges));*/
    }


    // face scan on +1 density buffer
    OpenFaces DetectOpenFaces(NativeArray<float> density)
    {
        OpenFaces flags = OpenFaces.None;
        int s = chunkSize + 1; // because we store a 1-voxel border

        // +X (x = s-1)
        for (int y = 0; y < s; y++)
            for (int z = 0; z < s; z++)
                if (density[(s - 1) + y * s + z * s * s] > isoLevel) { flags |= OpenFaces.PosX; goto NEG_X; }

            NEG_X:
        // -X (x = 0)
        for (int y = 0; y < s; y++)
            for (int z = 0; z < s; z++)
                if (density[0 + y * s + z * s * s] > isoLevel) { flags |= OpenFaces.NegX; goto POS_Y; }

            POS_Y:
        // +Y (y = s-1)
        for (int x = 0; x < s; x++)
            for (int z = 0; z < s; z++)
                if (density[x + (s - 1) * s + z * s * s] > isoLevel) { flags |= OpenFaces.PosY; goto NEG_Y; }

            NEG_Y:
        // -Y (y = 0)
        for (int x = 0; x < s; x++)
            for (int z = 0; z < s; z++)
                if (density[x + 0 * s + z * s * s] > isoLevel) { flags |= OpenFaces.NegY; goto POS_Z; }

            POS_Z:
        // +Z (z = s-1)
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                if (density[x + y * s + (s - 1) * s * s] > isoLevel) { flags |= OpenFaces.PosZ; goto NEG_Z; }

            NEG_Z:
        // -Z (z = 0)
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                if (density[x + y * s + 0 * s * s] > isoLevel) { flags |= OpenFaces.NegZ; }

        return flags;
    }

    void ScheduleGreedyMeshJob(int3 coord, NativeArray<float> nativeDensity)
    {
        ChunkProfiler.MeshStart(); // profiler hook

        // MeshData using Persistent allocator (safe beyond 4 frames)
        var meshData = new MeshData
        {
            coord = coord,
            vertices = new NativeList<float3>(Allocator.Persistent),
            triangles = new NativeList<int>(Allocator.Persistent),
            normals = new NativeList<float3>(Allocator.Persistent)
        };

        // Scratch buffer (short-lived)
        var scratch = new NativeArray<FMask>(chunkSize * chunkSize, Allocator.TempJob);

        var job = new GreedyMeshJob
        {
            density = nativeDensity,
            chunkSize = chunkSize,
            isoLevel = isoLevel,
            meshData = meshData,
            mask = scratch
        };

        // schedule async
        JobHandle handle = job.Schedule();
        pendingJobs.Add((handle, meshData, nativeDensity, scratch));

        ChunkProfiler.MeshEnd();
    }

    void ApplyMeshDataToChunk(MeshData meshData)
    {
        ChunkProfiler.UploadStart(); // PROFILER: start upload

        bool chunkEmpty = false;
        if (!meshData.vertices.IsCreated || meshData.vertices.Length == 0)
        {
            chunkEmpty = true;
        }

        // Find or create the target chunk if it does not exist
        chunks.TryGetValue(meshData.coord, out var chunk);
        if (chunk == null) // Try get value failed
        {
            if (!chunkEmpty) // Skips chunk object creation if the mesh is empty
            {
                GameObject go;

                // Either get from pool or create new
                if (chunkGoPool.Count > 0)
                {
                    go = chunkGoPool.Pop();
                    if (go == null) go = new GameObject();
                }
                else
                {
                    go = new GameObject();
                }

                go.name = $"Chunk_{meshData.coord.x}_{meshData.coord.y}_{meshData.coord.z}";
                go.transform.position = ChunkToWorld(meshData.coord);
                go.transform.parent = this.gameObject.transform;

                chunk = go.GetComponent<Chunk>() ?? go.AddComponent<Chunk>();
                chunk.chunkMaterial = chunkMaterial;

                chunks[meshData.coord] = chunk;
            }
            else
            {
                chunks[meshData.coord] = null; // Still add coords to chunks dictionary because it has been calculated
            }
        }

        // Apply mesh
        if (chunk != null)
            chunk.ApplyMesh(meshData);

        generating.Remove(meshData.coord);

        // Dispose mesh native lists (if not already disposed by ApplyMesh)
        if (meshData.vertices.IsCreated) meshData.vertices.Dispose();
        if (meshData.triangles.IsCreated) meshData.triangles.Dispose();
        if (meshData.normals.IsCreated) meshData.normals.Dispose();

        ChunkProfiler.UploadEnd(); // PROFILER: end upload
    }


    // Make this public for debug visualizers
    public Vector3 ChunkToWorld(int3 coord) =>
        new Vector3(coord.x, coord.y, coord.z) * chunkSize;

    int3 WorldToChunkCoord(Vector3 pos)
    {
        return new int3(
            Mathf.FloorToInt(pos.x / chunkSize),
            Mathf.FloorToInt(pos.y / chunkSize),
            Mathf.FloorToInt(pos.z / chunkSize)
        );
    }

    void OnDestroy()
    {
        // Complete and dispose pending jobs safely
        for (int i = pendingJobs.Count - 1; i >= 0; i--)
        {
            var (handle, meshData, density, scratch) = pendingJobs[i];
            if (!handle.IsCompleted) handle.Complete();

            // dispose scratch
            if (scratch.IsCreated) scratch.Dispose();

            // density: return to pool or dispose
            if (density.IsCreated)
            {
                lock (densityPool) { densityPool.Push(density); }
            }

            // Dispose mesh native lists (if not already disposed by ApplyMesh)
            if (meshData.vertices.IsCreated) meshData.vertices.Dispose();
            if (meshData.triangles.IsCreated) meshData.triangles.Dispose();
            if (meshData.normals.IsCreated) meshData.normals.Dispose();

            pendingJobs.RemoveAt(i);
        }

        // Dispose any leftover pooled arrays
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
    }


    void AdaptivePerformanceControl()
    {
        avgFrameTime = Mathf.Lerp(avgFrameTime, Time.deltaTime * 1000f, 0.1f);

        // Adaptive performance adjustion frequency
        if (Time.time - lastPerfAdjustTime < 0.1f)
            return;

        lastPerfAdjustTime = Time.time;

        const float dangerFrameTime = 17f;     // ~?60 FPS
        const float safeFrameTime = 8.3333f; // ?120 FPS

        bool adjusted = false;

        if (avgFrameTime > dangerFrameTime)
        {
            // Decrease load
            maxConcurrentNoiseTasks = Mathf.Max(1, maxConcurrentNoiseTasks - 1);
            maxMeshUploadsPerFrame = Mathf.Max(1, maxMeshUploadsPerFrame - 1);
            scheduleInterval = Mathf.Min(scheduleInterval * 1.15f, 0.4f);
            adjusted = true;
        }
        else if (avgFrameTime < safeFrameTime)
        {
            // Increase load
            maxConcurrentNoiseTasks = Mathf.Min(maxConcurrentNoiseTasks + 1, SystemInfo.processorCount);
            maxMeshUploadsPerFrame = Mathf.Min(maxMeshUploadsPerFrame + 1, 8);
            scheduleInterval = Mathf.Max(scheduleInterval * 0.85f, 0.05f);
            adjusted = true;
        }

        lock (densityPool)
        {
            if (densityPool.Count < 2)
            {
                var extra = new NativeArray<float>(densityCount, Allocator.Persistent);
                densityPool.Push(extra);
                initialPoolSize++;
                adjusted = true;
            }
        }

        if (adjusted)
        {
            //Debug.Log($"[PerfTuner] {avgFrameTime:F1}ms ? Noise={maxConcurrentNoiseTasks}, Uploads={maxMeshUploadsPerFrame}, Interval={scheduleInterval:F2}");
        }
    }

    void ReSeedFloodFrontier(int3 centerChunk)
    {
        float sqrViewDist = viewDistance * viewDistance;

        for (int x = -viewDistance; x <= viewDistance; x++)
            for (int y = -viewDistance; y <= viewDistance; y++)
                for (int z = -viewDistance; z <= viewDistance; z++)
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

}
