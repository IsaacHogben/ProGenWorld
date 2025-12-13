// ChunkManager.cs
// High-level orchestration for chunk generation, LOD updates, and async job scheduling.
// This file manages chunk lifecycle, noise tasks, block assignment jobs, mesh jobs,
// pooling, and flood-fill terrain expansion.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

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
    [SerializeField] public Material waterMaterial;

    [Header("LOD Settings")]
    [SerializeField] int nearRange = 4;
    [SerializeField] int midRange = 10;
    [SerializeField] int farRange = 20;

    [SerializeField] Material nearMaterial;
    [SerializeField] Material midMaterial;
    [SerializeField] Material farMaterial;

    // --------------------------------------------------
    // Performance Tuning
    // --------------------------------------------------

    [Header("Performance tuning")]
    public int maxConcurrentNoiseTasks = 4;   // How many noise tasks to allow concurrently - Increase until CPU saturates or frame drops.
    public int minConcurrentNoiseTasks = 2;
    public int maxMeshUploadsPerFrame = 2;    // How many mesh uploads (Mesh.SetVertices/SetTriangles) per frame - Increase until GPU upload hitching appears.
    public int minMeshUploadsPerFrame = 2;
    public int currentMeshUploadsPerFrame = 999;
    public float scheduleInterval = 1f;       // Seconds between scheduling passes - Lower until chunk popping delay is acceptable.
    public int meshDebounceFrames = 2;// Minimum # of frames between mesh jobs for any given chunk
    ProfilerRecorder gpuFrameTimeRecorder;

    [Header("Pre-allocation tuning")]
    // Configurable pool sizes (tweakable)
    [SerializeField] int maxDensityPoolPerLod = 32;
    [SerializeField] int prewarmDensityPerLod = 8;
    [SerializeField] int prewarmChunks = 200;
    [SerializeField] int prewarmMeshes = 400;
    // =====================================================
    // Adaptive Performance State
    // =====================================================

    // How often the tuner runs
    [SerializeField] float adaptiveInterval = 0.25f; // seconds
    float adaptiveLastTime = 0f;

    // Rolling averages (EMA)
    float emaFrameTime = 16.6f;     // ms
    float emaWorkerLoad = 0f;
    float emaGpuFrameTime = 0f;

    // Stability tracker for scale-up recovery
    float goodPerfTimer = 0f;
    float avgFrameTime = 16.6f;

    //Bootstrap
    bool bootstrapMode = true;
    string lastDecisionString = "Loading...";

    // --- Performance diagnostics ---
    public float AvgFrameTime => avgFrameTime;
    public int CurrentNoiseTasks => maxConcurrentNoiseTasks;
    public int CurrentMeshUploads => currentMeshUploadsPerFrame;
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
    readonly Dictionary<int3, int> lastMeshFrame = new();
    readonly Dictionary<int3, LODLevel> lodByCoord = new();
    readonly List<int3> chunksNeedingRemesh = new(256);
    readonly HashSet<int3> chunksNeedingRemeshSet = new HashSet<int3>(); // Chunks that need their mesh rebuilt this frame
    readonly HashSet<int3> chunksStillDecorating = new HashSet<int3>(); // Tracks chunks currently executing a DecorationJob

    // Threading / queues
    readonly ConcurrentQueue<(int3 coord, NativeArray<float> density, LODLevel lod)> completedNoiseQueue = new();
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
    static readonly List<int3> tmpChunkKeys = new List<int3>(256);
    static readonly List<int3> tmpChunksToRemove = new List<int3>(256);

    static readonly List<int3> tmpWriteChunks = new List<int3>(256);
    static readonly List<PendingBlockWrite> tmpWriteBuffer = new List<PendingBlockWrite>(512);


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

    // System
    private NoiseSystem noiseSystem;
    private BlockGenSystem blockGenSystem;
    private DecorationSystem decorationSystem;
    private WriteSystem writeSystem;
    private MeshSystem meshSystem;

    //private static readonly object EmptyChunkMarker = new object();

    void Awake()
    {
        nativeBlockDatabase = blockDatabase.ToNative(Allocator.Persistent);
        cts = new CancellationTokenSource();
        gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");

        InitializeSystems();

        // load texture array
        var texture = textureArrayBuilder.GetTextureArray();
        voxelMaterial.SetTexture("_Textures", texture);
        waterMaterial.SetTexture("_Textures", texture);

        //Define LODs
        lodConfigs[LODLevel.Near] = new LODSettings { meshRes = 1, sampleRes = 1, blockSize = 1, detailedBlocks = true, material = voxelMaterial };
        lodConfigs[LODLevel.Mid] = new LODSettings { meshRes = 2, sampleRes = 1, blockSize = 2, detailedBlocks = false, material = midMaterial };
        lodConfigs[LODLevel.Far] = new LODSettings { meshRes = 4, sampleRes = 1, blockSize = 4, detailedBlocks = false, material = farMaterial };
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

        lastPlayerPos = player.transform.position;
    }

    void Start()
    {
        // Sample settings
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        // Seed frontier at the player's current chunk
        InitFloodFrontier(player.transform.position, nearRange/2, 4);
    }

    void Update()
    {
        // Editor fix: forces GameView to detach from internal vsync
        #if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPlaying)
                UnityEngine.Rendering.OnDemandRendering.renderFrameInterval = 1;
        #endif

        ChunkProfiler.ActiveNoiseTasks = noiseSystem.ActiveTasks;
        ChunkProfiler.ActiveBlockJobs = blockGenSystem.ActiveJobs;
        ChunkProfiler.ActiveDecoJobs = decorationSystem.ActiveJobs;
        ChunkProfiler.ActiveMeshJobs = meshSystem.ActiveJobs;
        ChunkProfiler.PendingWriteChunks = writeSystem.PendingWritesCount;
        ChunkProfiler.TotalChunks = chunks.Count;

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

        // Trigger initial frontier propagation
        if (!firstFloodTriggered)
        {
            firstFloodTriggered = true;
            PromoteDeferredToFrontier(lastPlayerPos);
        }

        // Process phases only when needed
        if (movementTriggered)
            OnPlayerMovePhaseUpdate(lastPlayerPos);

        // Manage completed noise tasks, schedules block assignment
        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: DrainNoiseQueue");
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
            blockGenSystem.GenerateBlocks(data.coord, data.lod, data.density);
        }
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: BlockGenSystem.Update");
        blockGenSystem.Update();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: DecorationSystem.Update");
        decorationSystem.Update();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: FlushAllPendingWrites");
        FlushAllPendingWrites();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: RemeshNeedingChunks");
        RemeshChunksNeedingRemesh(); // extracted loop into its own method
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: MeshSystem.Update");
        meshSystem.Update();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: AdaptivePerformance");
        AdaptivePerformanceControl();
        UnityEngine.Profiling.Profiler.EndSample();
    }

    private void RemeshChunksNeedingRemesh()
    {
        // Re-mesh all chunks that received neighbour writes or decoration this frame
        if (chunksNeedingRemesh.Count > 0)
        {
            Profiler.StartRemeshLoop();
            tmpChunkKeys.Clear();
            for (int i = 0; i < chunksNeedingRemesh.Count; i++)
                tmpChunkKeys.Add(chunksNeedingRemesh[i]);

            for (int i = 0; i < tmpChunkKeys.Count; i++)
            {
                var coord = tmpChunkKeys[i];

                // Must have blockIds
                if (!fullResBlocksByChunk.TryGetValue(coord, out var blockIds) || !blockIds.IsCreated)
                {
                    Debug.LogWarning($"[BlockGen] Remesh attempted without full res at {coord}");
                    //generatingSet.Remove(coord);
                    continue;
                }

                // Current LOD (if chunk exists)
                LODLevel currentLod = LODLevel.Mid;
                if (chunks.TryGetValue(coord, out var existingChunk) && existingChunk != null)
                    currentLod = existingChunk.lod;

                // Compute what LOD we *actually want now* based on player distance
                LODLevel targetLod = GetLODForCoord(coord, lastPlayerPos, currentLod);

                // Debounce
                if (!CanMeshNow(coord))
                    continue;

                // Schedule through MeshSystem at the desired LOD
                meshSystem.RequestMesh(coord, blockIds, targetLod, keepBlocks: true, isWaterMesh: false);
            }

            chunksNeedingRemesh.Clear();
            chunksNeedingRemeshSet.Clear();
            Profiler.EndRemeshLoop();
        }
    }
    private void OnPlayerMovePhaseUpdate(Vector3 playerPos)
    {
        Profiler.StartMovePhase();
        switch (updatePhase)
        {
            case 0:
                CleanupDeferred(playerPos);
                PromoteDeferredToFrontier(playerPos);
                Profiler.EndMovePhase0();
                break;
            case 1:
                UpdateChunkLods(playerPos);
                Profiler.EndMovePhase1();
                break;
            case 2:
                UnloadChunks(playerPos);
                Profiler.EndMovePhase2();
                break;
            case 3:
                CullOutOfRangeBlockJobs(playerPos);
                Profiler.EndMovePhase3();
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
        Profiler.StartLodLoop();
        foreach (var kv in chunks)
        {
            int3 coord = kv.Key;
            Chunk chunk = kv.Value;

            LODLevel currentLod;
            if (chunk == null)
                currentLod = lodByCoord.TryGetValue(coord, out var storedLod) ? storedLod : LODLevel.Far;
            else
                currentLod = chunk.lod;

            LODLevel desired = GetLODForCoord(coord, playerPos, currentLod);

            if (desired == currentLod)
                continue;

            // Trigger regen only if not already generating
            if (!generatingSet.Contains(coord))
                EnqueueLodResolutionUpgrade(coord, desired);
        }
        Profiler.EndLodLoop();
    }
    void ScheduleNoiseTask(Vector3 playerPos)
    {
        Profiler.StartSchedLoop();
        if (player == null) return;

        int spawnedThisTick = 0;
        int cap = Math.Max(1, maxConcurrentNoiseTasks);

        // --- LOD upgrades / regen ---
        if (lodUpdateQueue.Count != 0)
        {
            while (spawnedThisTick < cap && TryDequeueLodUpdate(out var lodUpdateToken))
            {
                var coord = lodUpdateToken.coord;
                var targetLod = lodUpdateToken.lod;

                if (!chunks.ContainsKey(coord))
                    continue;

                // If target LOD uses full-res and we already have full-res blockIds,
                // there is no need to re-run density + blockgen: just remesh instead.
                if (lodConfigs[targetLod].sampleRes == 1 &&
                    fullResBlocksByChunk.TryGetValue(coord, out var existing) &&
                    existing.IsCreated)
                {
                    // Just mark for remesh; block data already exists
                    MarkChunkForRemesh(coord);
                    continue;
                }

                if (generatingSet.Contains(coord))
                    continue;

                generatingSet.Add(coord);
                spawnedThisTick++;
                noiseSystem.RequestDensity(coord, targetLod);
            }
        }

        // --- Frontier expansion (new chunks) ---
        if (frontierQueue.Count != 0)
        {
            while (spawnedThisTick < cap && TryDequeueFrontier(out var coord))
            {
                if (chunks.ContainsKey(coord) || generatingSet.Contains(coord))
                    continue;
                if (fullResBlocksByChunk.ContainsKey(coord))
                {
                    Debug.Log($"Frontier not queued at {coord} because a fullResBlocks was found");
                    continue;
                }

                generatingSet.Add(coord);
                spawnedThisTick++;
                LODLevel lod = GetLODForCoord(coord, playerPos, LODLevel.Mid);
                noiseSystem.RequestDensity(coord, lod);
            }
        }
        Profiler.EndSchedLoop();
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
    void PromoteDeferredToFrontier(Vector3 playerPos) // Player changed chunk — promote deferred coords that are now in range
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
    private OpenFaces DetectTerrainFlowFromBlocks(NativeArray<byte> blockIds, LODLevel lod)
    {
        int sampleRes = lodConfigs[lod].sampleRes;

        int s = (chunkSize / sampleRes) + 1;
        OpenFaces flags = OpenFaces.None;

        bool IsSolid(int x, int y, int z)
            => nativeBlockDatabase[blockIds[x + y * s + z * s * s]].isSolid; // non-air = solid
        bool IsSeeThrough(int x, int y, int z)
            => nativeBlockDatabase[blockIds[x + y * s + z * s * s]].visibility != BlockVisibility.Opaque; // Anything that might allow vision into neigbour chunk

        bool FaceExposed(Func<int, int, bool> solidCheck, Func<int, int, bool> seeThroughCheck)
        {
            bool sawSolid = false;
            bool sawTransparent = false;

            // sample every Nth voxel depending on LOD meshRes
            for (int i = 0; i < s; i += sampleRes)
            {
                for (int j = 0; j < s; j += sampleRes)
                {

                    if (solidCheck(i, j)) sawSolid = true;
                    if (seeThroughCheck(i, j)) sawTransparent = true;

                    if (sawSolid && sawTransparent)
                        return true; // mixed = exposed
                }
            }
            return false; // fully solid or fully air = not exposed
        }

        // +X
        if (FaceExposed((y, z) => IsSolid(s - 1, y, z), (y, z) => IsSeeThrough(s - 1, y, z)))
            flags |= OpenFaces.PosX;

        // -X
        if (FaceExposed((y, z) => IsSolid(0, y, z), (y, z) => IsSeeThrough(0, y, z)))
            flags |= OpenFaces.NegX;

        // +Y
        if (FaceExposed((x, z) => IsSolid(x, s - 1, z), (x, z) => IsSeeThrough(x, s - 1, z)))
            flags |= OpenFaces.PosY;

        // -Y
        if (FaceExposed((x, z) => IsSolid(x, 0, z), (x, z) => IsSeeThrough(x, 0, z)))
            flags |= OpenFaces.NegY;

        // +Z
        if (FaceExposed((x, y) => IsSolid(x, y, s - 1), (x, y) => IsSeeThrough(x, y, s - 1)))
            flags |= OpenFaces.PosZ;

        // -Z
        if (FaceExposed((x, y) => IsSolid(x, y, 0), (x, y) => IsSeeThrough(x, y, 0)))
            flags |= OpenFaces.NegZ;

        return flags;
    }
    void AdaptivePerformanceControl()
    {
        // ------------------------------------------------------------
        // 1. Fixed-rate Update
        // ------------------------------------------------------------
        if (Time.time - adaptiveLastTime < adaptiveInterval)
            return;
        adaptiveLastTime = Time.time;

        float frameMs = Time.deltaTime * 1000f;

        // EMAs
        const float alpha = 0.12f;
        emaFrameTime = Mathf.Lerp(emaFrameTime, frameMs, alpha);

        int activeNoise = noiseSystem.ActiveTasks;
        int activeMesh = meshSystem.ActiveJobs;
        int activeDeco = decorationSystem.ActiveJobs;

        int coreCount = Mathf.Max(1, SystemInfo.processorCount);
        float workerLoadSample = Mathf.Clamp01((activeNoise + activeMesh + activeDeco) / (float)coreCount);
        emaWorkerLoad = Mathf.Lerp(emaWorkerLoad, workerLoadSample, alpha);

        // GPU timing optional
        float gpuMs = 0f;
        if (gpuFrameTimeRecorder.Valid && gpuFrameTimeRecorder.LastValue > 0)
        {
            gpuMs = gpuFrameTimeRecorder.LastValue / 1_000_000f;
            if (emaGpuFrameTime <= 0f) emaGpuFrameTime = gpuMs;
            else emaGpuFrameTime = Mathf.Lerp(emaGpuFrameTime, gpuMs, alpha);
        }

        // ------------------------------------------------------------
        // 2. Bootstrap mode — no throttling until world bubble is done
        // ------------------------------------------------------------
        if (bootstrapMode)
        {
            maxConcurrentNoiseTasks = coreCount;
            currentMeshUploadsPerFrame = maxMeshUploadsPerFrame;
            scheduleInterval = 0.001f;
            meshDebounceFrames = 0;

            bool worldDone =
                frontierQueue.Count == 0 &&
                activeNoise == 0 &&
                activeMesh == 0 &&
                activeDeco == 0;

            if (worldDone)
            {
                bootstrapMode = false;
                Debug.Log("Initial Loading Complete!");
            }

            return;
        }

        // ------------------------------------------------------------
        // 3. Push state into profiler
        // ------------------------------------------------------------
        ChunkProfiler.AP_EmaFrameTime = emaFrameTime;
        ChunkProfiler.AP_EmaWorkerLoad = emaWorkerLoad;
        ChunkProfiler.AP_EmaGpuTime = emaGpuFrameTime;

        bool cpuStressed = emaFrameTime > ChunkProfiler.AP_WarnThreshold;
        bool gpuStressed = emaGpuFrameTime > ChunkProfiler.AP_WarnThreshold;
        bool workerStressed = emaWorkerLoad > 0.7f;

        ChunkProfiler.AP_IsCPUStressed = cpuStressed;
        ChunkProfiler.AP_IsGPUStressed = gpuStressed;
        ChunkProfiler.AP_IsWorkerStressed = workerStressed;

        // ------------------------------------------------------------
        // 4. Backpressure (queue overload)
        // ------------------------------------------------------------
        int backlog =
            frontierQueue.Count +
            chunksNeedingRemesh.Count +
            lodUpdateQueue.Count;

        bool backlogStressed = backlog > 200;
        bool stressed =
            backlogStressed ||
            cpuStressed ||
            gpuStressed ||
            workerStressed;

        if (stressed) goodPerfTimer = 0f;
        else goodPerfTimer += adaptiveInterval;

        // ------------------------------------------------------------
        // 5. Backpressure overrides everything else
        // ------------------------------------------------------------
        if (backlogStressed)
        {
            maxConcurrentNoiseTasks = Mathf.Max(minConcurrentNoiseTasks, 1);
            currentMeshUploadsPerFrame = Mathf.Max(minMeshUploadsPerFrame, 1);

            scheduleInterval = Mathf.Min(scheduleInterval * 1.15f, 0.30f);
            meshDebounceFrames = Mathf.Min(meshDebounceFrames + 1, 10);

            lastDecisionString = "Backpressure Slowdown";
            ChunkProfiler.AP_LastDecision = lastDecisionString;
            return;
        }

        // ------------------------------------------------------------
        // 6. CPU/GPU Stress Adjustments
        // ------------------------------------------------------------
        if (cpuStressed || gpuStressed)
        {
            currentMeshUploadsPerFrame = Mathf.Max(minMeshUploadsPerFrame, currentMeshUploadsPerFrame - 1);
            scheduleInterval = Mathf.Min(scheduleInterval * 1.10f, 0.40f);
            meshDebounceFrames = Mathf.Min(meshDebounceFrames + 1, 8);

            lastDecisionString = "CPU/GPU Throttle";
            ChunkProfiler.AP_LastDecision = lastDecisionString;
        }

        // ------------------------------------------------------------
        // 7. Worker Saturation
        // ------------------------------------------------------------
        if (workerStressed)
        {
            maxConcurrentNoiseTasks =
                Mathf.Max(minConcurrentNoiseTasks, maxConcurrentNoiseTasks - 1);

            lastDecisionString = "Worker Saturation";
            ChunkProfiler.AP_LastDecision = lastDecisionString;
        }

        // ------------------------------------------------------------
        // 8. Recovery Mode
        // ------------------------------------------------------------
        if (!stressed && goodPerfTimer > 1.0f)
        {
            maxConcurrentNoiseTasks =
                Mathf.Min(maxConcurrentNoiseTasks + 1, coreCount);

            currentMeshUploadsPerFrame =
                Mathf.Min(currentMeshUploadsPerFrame + 1, maxMeshUploadsPerFrame);

            scheduleInterval =
                Mathf.Max(scheduleInterval * 0.92f, 0.05f);

            meshDebounceFrames =
                Mathf.Max(2, meshDebounceFrames - 1);

            lastDecisionString = "Recovery Speed-Up";
            ChunkProfiler.AP_LastDecision = lastDecisionString;
        }

        ChunkProfiler.AP_LastDecisionTime = Time.time;
    }
    void InitFloodFrontier(Vector3 playerPos, int radius, int height) // Create the initial position for flood fill to begin. Is the players location at start of world load.
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
    void UnloadChunks(Vector3 currentPlayerChunk)
    {    
        if (deferredFrontier.Count > 0)
        {
            tmpChunkKeys.Clear();
            tmpChunkKeys.AddRange(chunks.Keys);

            tmpChunksToRemove.Clear();

            for (int i = 0; i < tmpChunkKeys.Count; i++)
            {
                int3 coord = tmpChunkKeys[i];

                if (GetDistanceAtChunkScaleWithRenderShape(coord, currentPlayerChunk) > unloadDistance && !generatingSet.Contains(coord))
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
    void CleanupDeferred(Vector3 playerPos)
    {
        tmpFrontierList.Clear();
        tmpFrontierList.AddRange(deferredFrontier);

        float limit = unloadDistance + 1;

        foreach (var coord in tmpFrontierList)
        {
            if (GetDistanceAtChunkScaleWithRenderShape(coord, playerPos) > limit)
                deferredFrontier.Remove(coord);
        }
    }
    private void InitializeSystems()
    {
        // NoiseSystem set-up
        noiseSystem = new NoiseSystem();
        noiseSystem.Initialize(
            new NoiseSystem.NoiseConfig
            {
                seed = seed,
                frequency = frequency,
                chunkSize = chunkSize,
                maxConcurrentNoiseTasks =  maxConcurrentNoiseTasks
            },
            lod => RentDensityArray(lod),
            (lod, arr) => ReturnDensityArray(lod, arr)
        );
        noiseSystem.OnDensityReady += HandleDensityReady;

        // BlockGenSystem set-up
        blockGenSystem = new BlockGenSystem();
        blockGenSystem.Initialize(
            new BlockGenSystem.Config
            {
                chunkSize = chunkSize,
                GetSampleRes = lod => lodConfigs[lod].sampleRes
            },
            (lod, density) => ReturnDensityArray(lod, density)
        );

        // When blocks are ready
        blockGenSystem.OnBlockGenCompleted += HandleBlockGenCompleted;

        // DecorationSystem set-up
        decorationSystem = new DecorationSystem();
        decorationSystem.Initialize(new DecorationSystem.DecorationConfig
        {
            chunkSize = chunkSize,
            indexSize = chunkSize + 1,
            seed = seed,
            GetBootstrapMode = () => bootstrapMode
        });
        decorationSystem.OnDecorationStarted += HandleDecorationStarted;
        decorationSystem.OnDecorationCompleted += HandleDecorationCompleted;

        // PendingWriteSystem set-up
        writeSystem = new WriteSystem();
        writeSystem.Initialize(
            chunkSize,
            coord => chunksStillDecorating.Contains(coord),
            coord => meshSystem.IsMeshInProgress(coord),
            coord => fullResBlocksByChunk.ContainsKey(coord) && fullResBlocksByChunk[coord].IsCreated,
            coord => fullResBlocksByChunk[coord],
            coord => MarkChunkForRemesh(coord),
            coord => GetDistanceAtChunkScaleWithRenderShape(coord, lastPlayerPos),    // getDistance()
            viewDistance,                                                             // forceGenRange
            EnqueueFrontier                                                           // queueFrontier()
        );

        // MeshSystem set-up      
        meshSystem = new MeshSystem();
        meshSystem.Initialize(
            new MeshSystem.Config
            {
                chunkSize = chunkSize,
                blockDb = nativeBlockDatabase,
                GetMeshRes = lod => lodConfigs[lod].meshRes,
                GetSampleRes = lod => lodConfigs[lod].sampleRes,
                GetBlockSize = lod => lodConfigs[lod].blockSize,
                GetMaxUploadsPerFrame = () => currentMeshUploadsPerFrame
            },
            rentMesh: () =>
            {
                if (meshPool.Count > 0)
                    return meshPool.Pop();
                return new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            },
            returnMesh: mesh =>
            {
                mesh.Clear();
                meshPool.Push(mesh);
            },
            allocMeshData: () =>
            {
                return new MeshData
                {
                    vertices = new NativeList<float3>(Allocator.Persistent),
                    triangles = new NativeList<int>(Allocator.Persistent),
                    normals = new NativeList<float3>(Allocator.Persistent),
                    colors = new NativeList<float4>(Allocator.Persistent),
                    UV0s = new NativeList<float2>(Allocator.Persistent),
                    isWaterMesh = new NativeReference<bool>(Allocator.Persistent),
                    requestWaterMesh = new NativeReference<bool>(Allocator.Persistent)
                };
            },
            freeMeshData: data =>
            {
                if (data.vertices.IsCreated) data.vertices.Dispose();
                if (data.triangles.IsCreated) data.triangles.Dispose();
                if (data.normals.IsCreated) data.normals.Dispose();
                if (data.colors.IsCreated) data.colors.Dispose();
                if (data.UV0s.IsCreated) data.UV0s.Dispose();
                if (data.isWaterMesh.IsCreated) data.requestWaterMesh.Dispose();
                if (data.requestWaterMesh.IsCreated) data.requestWaterMesh.Dispose();
            },
            markMeshed: coord => lastMeshFrame[coord] = Time.frameCount,
            canMeshNow: coord => CanMeshNow(coord)
        );
        meshSystem.OnMeshReady += HandleMeshReady;
    }

    // =============================
    // ====== System Handles =======
    // =============================
    private void CompleteGeneration(int3 coord)
    {
        generatingSet.Remove(coord);
    }
    private void HandleMeshReady(int3 coord, MeshData meshData)
    {
        bool empty = !meshData.vertices.IsCreated || meshData.vertices.Length == 0;
        lodByCoord[coord] = meshData.lod;
        // Try get existing chunk entry
        chunks.TryGetValue(coord, out var chunk);

        if (empty)
        {
            // Assign
            chunks[coord] = null;
            // No mesh to apply just cleanup - same as below
            meshSystem.ReleaseMeshData(meshData);
            generatingSet.Remove(coord);
            deferredFrontier.Remove(coord);

            return;
        }

        // Create chunk GO only if missing
        if (chunk == null)
        {
            GameObject go = (chunkGoPool.Count > 0) ? chunkGoPool.Pop() : new GameObject();
            go.SetActive(true);
            go.name = $"Chunk_{coord.x}_{coord.y}_{coord.z}";
            go.transform.position = ChunkToWorld(coord);
            go.transform.parent = transform;

            chunk = go.GetComponent<Chunk>();
            if (chunk == null)
                chunk = go.AddComponent<Chunk>();

            chunks[coord] = chunk;
        }
        if (meshData.isWaterMesh.Value == true)
        {
            chunk.waterMaterial = waterMaterial;
            chunk.ApplyWaterMesh(meshData, meshPool);
        }
        else // Apply mesh normally
        {   
            chunk.chunkMaterial = voxelMaterial;
            chunk.lod = meshData.lod;
            chunk.ApplyMesh(meshData, meshPool);
        }

        if (meshData.requestWaterMesh.Value == meshData.isWaterMesh.Value) // This means we are still waiting on a water mesh
        {
            meshSystem.ReleaseMeshData(meshData);
            generatingSet.Remove(coord);
            deferredFrontier.Remove(coord);
        }
    }
    private void HandleDecorationCompleted(int3 coord, NativeList<PendingBlockWrite> writes)
    {
        chunksStillDecorating.Remove(coord);

        if (!fullResBlocksByChunk.TryGetValue(coord, out var blockIds) || !blockIds.IsCreated)
        {
            Debug.LogError($"[Decoration] Completed for {coord} but no fullResBlocksByChunk exists.");

            for (int i = 0; i < writes.Length; i++)
                writeSystem.EnqueueWrite(writes[i]);

            // we still own the native list: dispose it
            if (writes.IsCreated)
                writes.Dispose();

            return;
        }

        // Enqueue all writes to WriteSystem
        for (int i = 0; i < writes.Length; i++)
            writeSystem.EnqueueWrite(writes[i]);

        // Done with native container
        if (writes.IsCreated)
            writes.Dispose();

        MarkChunkForRemesh(coord);

        var faces = DetectTerrainFlowFromBlocks(blockIds, LODLevel.Near);
        ExpandFrontierFromFaces(coord, player.transform.position, faces);
    }
    private void HandleDecorationStarted(int3 coord)
    {
        chunksStillDecorating.Add(coord);
    }
    private void HandleBlockGenCompleted(int3 coord, LODLevel lod, NativeArray<byte> blockIds)
    {
        int sampleRes = lodConfigs[lod].sampleRes;

        // --- Full-res: Near & Mid only ---
        if (sampleRes == 1)
        {
            // If we already have a full-res block array for this chunk,
            // this generation was redundant. Dispose the new one and bail.
            if (fullResBlocksByChunk.TryGetValue(coord, out var existing) && existing.IsCreated)
            {
                Debug.LogWarning($"[BlockGen] Redundant full-res blockIds for {coord}, discarding new buffer.");

                if (blockIds.IsCreated)
                    blockIds.Dispose();

                generatingSet.Remove(coord); // this regen pass is done
                return;
            }

            // Normal case: first time full-res is generated for this chunk
            fullResBlocksByChunk[coord] = blockIds;

            // Run decoration on full-res chunks only
            decorationSystem.ScheduleDecoration(coord, lod, blockIds);
            return;
        }

        // --- Test for skipping Decoration ---
        {
            var faces = DetectTerrainFlowFromBlocks(blockIds, lod);
            ExpandFrontierFromFaces(coord, lastPlayerPos, faces);

            // Far LOD - don't keep blocks; MeshSystem frees them after mesh
            meshSystem.RequestMesh(coord, blockIds, lod, keepBlocks: true, isWaterMesh: false);
        }
    }
    private void HandleDensityReady(int3 coord, LODLevel lod, NativeArray<float> density)
    {
        completedNoiseQueue.Enqueue((coord, density, lod));
    }

    // =============================
    // ===== HELPER FUNCTIONS ======
    // =============================
    public void MarkChunkForRemesh(int3 coord)
    {
        if (chunksNeedingRemeshSet.Add(coord))
            chunksNeedingRemesh.Add(coord);
    }
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

    // temp buffer – allocated once at class scope
    private static readonly List<int3> tmpWriteCoords = new List<int3>(256);
    void FlushAllPendingWrites()
    {
        // Early-out: nothing queued
        if (writeSystem.PendingWritesCount == 0)
            return;

        // Collect coords without allocs
        writeSystem.GetKeySnapshot(tmpWriteCoords);

        for (int i = 0; i < tmpWriteCoords.Count; i++)
        {
            int3 coord = tmpWriteCoords[i];

            // Detach current batch for this coord
            var batch = writeSystem.StealWrites(coord);
            if (batch == null || batch.Count == 0)
            {
                writeSystem.RecycleList(batch);
                continue;
            }

            // Process this snapshot only – re-enqueued writes go to a NEW list
            for (int w = 0; w < batch.Count; w++)
            {
                writeSystem.ProcessWrite(coord, batch[w]);
            }

            // Return this list to the pool
            writeSystem.RecycleList(batch);
        }
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
    void EnqueueLodResolutionUpgrade(int3 c, LODLevel l) // Currently does not do any extra sorting
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
        blockGenSystem.CullOutOfRange(
            center,
            viewDistance + 1, // small buffer
            coord => GetDistanceAtChunkScaleWithRenderShape(coord, center),
            coord => CompleteGeneration(coord)
        );
    }
    void ReleaseChunk(int3 coord)
    {
        deferredFrontier.Add(coord); // If a chunk is removed it must then become a new frontier for terrain generation

        if (!chunks.TryGetValue(coord, out var chunk))
            return;

        // Handle null seperatly here as empty chunks can exist in the dictionary
        if (chunk != null)
        {    
            chunk.Release(meshPool);
            chunkGoPool.Push(chunk.gameObject);
        }

        chunks.Remove(coord);

        if (fullResBlocksByChunk.TryGetValue(coord, out var arr))
        {
            if (arr.IsCreated) arr.Dispose();
            fullResBlocksByChunk.Remove(coord);
        }

        // Also clear any “future” writes never applied
        writeSystem.Clear(coord);
        lodByCoord.Remove(coord);
        chunksNeedingRemesh.Remove(coord);
    }
    public Vector3 ChunkToWorld(int3 coord)
    {
        return ChunkCoordUtils.ChunkToWorld(coord, chunkSize);
    }
    public Vector3 ChunkToWorldCenter(int3 coord)
    {
        return ChunkCoordUtils.ChunkToWorldCenter(coord, chunkSize);
    }
    public bool IsChunkWithinRange(int3 chunkCoord, Vector3 playerChunk, float viewDistance)
    {
        return GetDistanceAtChunkScaleWithRenderShape(chunkCoord, playerChunk) <= viewDistance;
    }
    float GetDistanceAtChunkScaleWithRenderShape(int3 _chunk, Vector3 pos)
    {
        return ChunkCoordUtils.GetChunkDistance(_chunk, pos, chunkSize, renderShape);    
    }
    int3 WorldToChunkCoord(Vector3 pos)
    {
        return ChunkCoordUtils.WorldToChunkCoord(pos, chunkSize);
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
    void CompleteAllJobs()
    {
        blockGenSystem.Shutdown();
        //decorationSystem.Shutdown();
        //meshSystem.Shutdown();
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
        generatingSet.Clear();
        //activeNoiseTasks.Clear();

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
        writeSystem.ClearAll();
        chunksNeedingRemesh.Clear();

        // Dispose block database
        if (nativeBlockDatabase.IsCreated)
            nativeBlockDatabase.Dispose();

        noiseSystem.Shutdown();
        // Dispose compute detector
        openFaceDetector?.Dispose();
        if (gpuFrameTimeRecorder.Valid)
            gpuFrameTimeRecorder.Dispose();

    }
}
