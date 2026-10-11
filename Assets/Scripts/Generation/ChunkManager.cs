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
using static Unity.Burst.Intrinsics.X86.Avx;

public class ChunkManager : MonoBehaviour, IVoxelWorldSource
{
    // ==================================================
    // World Configuration
    // ==================================================

    [Header("World")]
    public int seed = 1337;
    public float frequency = 1;
    public int chunkSize = 32;

    [SerializeField] int minMoveBeforeUpdate = 16;
    [SerializeField] RenderShape renderShape = RenderShape.Sphere;

    public bool unloadAtMaxChunks = false;
    [SerializeField] int maxChunks;         // Max chunks loaded before most distant chunks begin to unload
    [SerializeField] int chunkUnloadBuffer; // How many more chunks than max before chunk unload begins - prevents chunk flicker


    // ==================================================
    // Scene References
    // ==================================================

    [Header("References")]
    public GameObject player;

    [SerializeField] public BiomeSystem biomeSystem;
    [SerializeField] public BiomeDataManager biomeDataManager;

    [SerializeField] private BlockDatabase blockDatabase;
    [SerializeField] public BlockTextureArrayBuilder textureArrayBuilder;

    [Tooltip("Voxel rendering / shadow / texture settings. Found or created automatically when empty.")]
    [SerializeField] VoxelRenderSettings renderSettings;

    [Tooltip("View distance, LOD ranges and the fog linked to them. Found or created automatically when empty.")]
    [SerializeField] ViewDistanceSettings viewSettings;


    // ==================================================
    // View Distance / LOD (from ViewDistanceSettings)
    // ==================================================

    public int viewDistance => viewSettings.viewDistance;
    public int unloadPastViewDistance => viewSettings.unloadPastViewDistance;
    private int unloadDistance => viewSettings.UnloadDistance;
    int nearRange => viewSettings.nearRange;
    int midRange => viewSettings.midRange;


    // ==================================================
    // Performance Tuning
    // ==================================================

    [Header("Performance Tuning")]
    public int maxConcurrentSchedualTasks = 4;   // How many noise tasks to allow concurrently - Increase until CPU saturates or frame drops.
    public int minConcurrentNoiseTasks = 2;

    public int maxMeshUploadsPerFrame = 2;    // How many mesh uploads per frame - Increase until GPU upload hitching appears.
    public int minMeshUploadsPerFrame = 2;
    public int currentMeshUploadsPerFrame = 999;

    public float scheduleInterval = 1f;       // Seconds between scheduling passes - Lower until chunk popping delay is acceptable.
    public int meshDebounceFrames = 2;         // Minimum number of frames between mesh jobs for any given chunk

    ProfilerRecorder gpuFrameTimeRecorder;


    // ==================================================
    // Pre-allocation Tuning
    // ==================================================

    [Header("Pre-allocation Tuning")]
    [SerializeField] int maxDensityPoolPerLod = 32;
    [SerializeField] int prewarmDensityPerLod = 8;


    // ==================================================
    // Adaptive Performance State (Runtime)
    // ==================================================

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

    // Bootstrap
    bool bootstrapMode = true;
    string lastDecisionString = "Loading...";

    // --- Performance diagnostics ---
    public float AvgFrameTime => avgFrameTime;
    public int CurrentNoiseTasks => maxConcurrentSchedualTasks;
    public int CurrentMeshUploads => currentMeshUploadsPerFrame;
    public float CurrentScheduleInterval => scheduleInterval;
    public int CurrentPoolSize => 404;


    // ==================================================
    // Chunk / Generation State
    // ==================================================

    readonly Dictionary<int3, Chunk> chunks = new();
    readonly HashSet<int3> generatingSet = new();   // coords reserved for generation or meshing

    readonly Dictionary<LODLevel, LODSettings> lodConfigs = new();
    readonly Dictionary<int3, NativeArray<byte>> fullResBlocksByChunk = new();
    readonly Dictionary<int3, int> lastMeshFrame = new();
    readonly Dictionary<int3, LODLevel> lodByCoord = new();

    readonly List<int3> chunksNeedingRemesh = new(256);
    readonly HashSet<int3> chunksNeedingRemeshSet = new(); // Chunks that need their mesh rebuilt this frame
    readonly HashSet<int3> chunksStillDecorating = new();  // Chunks currently executing a DecorationJob


    // ==================================================
    // Biome State
    // ==================================================

    readonly Dictionary<int3, BiomeData> biomeDataByChunk = new();
    readonly HashSet<int3> pendingBiome = new();


    // ==================================================
    // Threading / Queues
    // ==================================================

    readonly ConcurrentQueue<(int3 coord, NativeArray<float> density, LODLevel lod)> completedNoiseQueue = new();
    CancellationTokenSource cts;


    // ==================================================
    // Flood-Fill System
    // ==================================================

    readonly Queue<int3> frontierQueue = new();      // chunks waiting to be expanded
    readonly HashSet<int3> frontierSet = new();      // hash set for duplication checks

    readonly HashSet<int3> deferredFrontier = new(); // neighbors to expand later (out of range)
    private bool firstFloodTriggered = false;

    public HashSet<int3> GetDeferredFrontier() => deferredFrontier;

    struct LodUpdateRequest
    {
        public int3 coord;
        public LODLevel lod;
    }

    readonly Queue<LodUpdateRequest> lodUpdateQueue = new();
    readonly HashSet<int3> lodUpdateSet = new();


    // ==================================================
    // Temporary Buffers (GC Avoidance)
    // ==================================================

    static readonly List<int3> tmpFrontierList = new(512);
    static readonly List<int3> tmpFrontierRemove = new(128);
    static readonly List<int3> tmpChunkKeys = new(256);
    static readonly List<int3> tmpChunksToRemove = new(256);

    static readonly List<int3> tmpWriteChunks = new(256);
    static readonly List<PendingBlockWrite> tmpWriteBuffer = new(512);


    // ==================================================
    // Resources / Native Data
    // ==================================================

    [Header("Resources")]
    private NativeArray<BlockDatabase.BlockInfoUnmanaged> nativeBlockDatabase;
    private OpenFaceDetector openFaceDetector;  // kernel


    // ==================================================
    // Pooling System
    // ==================================================

    readonly Dictionary<LODLevel, Stack<NativeArray<float>>> densityPoolByLod = new();
    readonly object densityPoolLock = new();



    // ==================================================
    // Timing / Movement State
    // ==================================================

    float lastScheduleTime = -999f;
    bool movementTriggered = false;
    int updatePhase = 0;
    Vector3 lastPlayerPos;


    // ==================================================
    // Systems
    // ==================================================

    private NoiseSystem noiseSystem;
    private BlockGenSystem blockGenSystem;
    private DecorationSystem decorationSystem;
    private WriteSystem writeSystem;
    private VoxelPackSystem packSystem;
    private VoxelWorldRenderer voxelRenderer;

    public VoxelWorldRenderer VoxelRenderer => voxelRenderer;

    void Awake()
    {
        if (viewSettings == null)
            viewSettings = ViewDistanceSettings.FindOrCreate();

        nativeBlockDatabase = blockDatabase.ToNative(Allocator.Persistent);
        cts = new CancellationTokenSource();
        gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time");

        InitializeSystems();

        // GPU voxel storage + raymarch rendering, voxel collision queries
        InitializeVoxelRenderer();
        VoxelPhysics.Source = this;

        // Define LODs. Block data is always generated at full resolution; voxelScale is the size of
        // the GPU detail level the renderer draws at this distance.
        lodConfigs[LODLevel.Near] = new LODSettings { voxelScale = 1, sampleRes = 1 };
        lodConfigs[LODLevel.Mid] = new LODSettings { voxelScale = 2, sampleRes = 1 };
        lodConfigs[LODLevel.Far] = new LODSettings { voxelScale = 4, sampleRes = 1 };
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

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: ProfilerStats");
        ChunkProfiler.ActiveNoiseTasks = noiseSystem.ActiveTasks;
        ChunkProfiler.ActiveBlockJobs = blockGenSystem.ActiveJobs;
        ChunkProfiler.ActiveDecoJobs = decorationSystem.ActiveJobs;
        ChunkProfiler.ActiveMeshJobs = packSystem.ActiveJobs;
        ChunkProfiler.PendingWriteChunks = writeSystem.PendingWritesCount;
        ChunkProfiler.TotalChunks = chunks.Count;
        UnityEngine.Profiling.Profiler.EndSample();

        // --- Flood-fill activation when entering new chunk ---
        Vector3 currentPlayerPos = player.transform.position;
        float moved = math.distance(currentPlayerPos, lastPlayerPos);

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: BiomeSystem.Updater");
        biomeSystem.Updater();
        UnityEngine.Profiling.Profiler.EndSample();

        // Periodic scheduling (throttled)
        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: InjectChunkRequestIntoPipeline, Throttled");
        if (Time.time - lastScheduleTime >= scheduleInterval)
        {
            lastScheduleTime = Time.time;

            if (frontierQueue.Count > 0 || lodUpdateQueue.Count > 0)
                InjectChunkRequestIntoPipeline(currentPlayerPos);
        }
        UnityEngine.Profiling.Profiler.EndSample();

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
            blockGenSystem.GenerateBlocks(data.coord, data.lod, data.density, biomeDataByChunk[data.coord]);
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
        RemeshChunksNeedingRemesh();
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("ChunkManager: VoxelPackSystem.Update");
        packSystem.Update();
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

                // Schedule a GPU repack at the desired LOD (debounced requests are queued, not dropped)
                packSystem.RequestPack(coord, blockIds, targetLod);
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
                EnqueueLodUpdate(coord, desired);
        }
        Profiler.EndLodLoop();
    }
    void InjectChunkRequestIntoPipeline(Vector3 playerPos)
    {
        Profiler.StartSchedLoop();
        if (player == null) return;

        int spawnedThisTick = 0;
        int cap = Math.Max(1, maxConcurrentSchedualTasks);

        UnityEngine.Profiling.Profiler.BeginSample("InjectChunk: LOD Updates");
        if (lodUpdateQueue.Count != 0)
        {
            while (spawnedThisTick < cap && TryDequeueLodUpdate(out var lodUpdateToken))
            {
                var coord = lodUpdateToken.coord;
                var targetLod = lodUpdateToken.lod;

                if (!chunks.ContainsKey(coord))
                    continue;

                // Block data already exists at full resolution: the GPU holds every detail level,
                // so a LOD change is just bookkeeping (no regeneration, no re-upload).
                if (lodConfigs[targetLod].sampleRes == 1 &&
                    fullResBlocksByChunk.TryGetValue(coord, out var existing) &&
                    existing.IsCreated)
                {
                    ApplyLodChange(coord, targetLod);
                    continue;
                }

                if (generatingSet.Contains(coord))
                    continue;

                // If target chunk did not previously exist due to low resolution,
                // schedual it now
                EnsureBiome(coord);
                generatingSet.Add(coord);
                spawnedThisTick++;
            }
        }
        UnityEngine.Profiling.Profiler.EndSample();

        UnityEngine.Profiling.Profiler.BeginSample("InjectChunk: Frontier Queue");
        if (frontierQueue.Count != 0)
        {
            UnityEngine.Profiling.Profiler.BeginSample("Frontier: Dequeue Loop");
            int frontierIterations = 0;
            while (spawnedThisTick < cap && TryDequeueFrontier(out var coord))
            {
                frontierIterations++;

                UnityEngine.Profiling.Profiler.BeginSample("Frontier: Dict Lookups");
                bool chunkExists = chunks.ContainsKey(coord);
                bool isGenerating = generatingSet.Contains(coord);
                UnityEngine.Profiling.Profiler.EndSample();

                if (chunkExists || isGenerating)
                    continue;

                UnityEngine.Profiling.Profiler.BeginSample("Frontier: EnsureBiome");
                EnsureBiome(coord);
                UnityEngine.Profiling.Profiler.EndSample();

                UnityEngine.Profiling.Profiler.BeginSample("Frontier: Add to GeneratingSet");
                generatingSet.Add(coord);
                UnityEngine.Profiling.Profiler.EndSample();

                spawnedThisTick++;
            }
            UnityEngine.Profiling.Profiler.EndSample();

            // DIAGNOSTIC
            if (frontierIterations > 100)
                Debug.LogWarning($"[FRONTIER SPIKE] Iterated {frontierIterations} times, spawned {spawnedThisTick}");
        }
        UnityEngine.Profiling.Profiler.EndSample();

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

        // The flood fill never reaches chunks without terrain (e.g. open sky), so generate the
        // ones neighbouring decorations wrote into once they come into range.
        writeSystem.GetTargetsAwaitingBlocks(tmpWriteTargets);
        foreach (var c in tmpWriteTargets)
        {
            if (!chunks.ContainsKey(c) && !generatingSet.Contains(c) && IsChunkWithinRange(c, playerPos, viewDistance))
                EnqueueFrontier(c);
        }
    }
    private readonly List<int3> tmpWriteTargets = new();
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
        int activeMesh = packSystem.ActiveJobs;
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
            maxConcurrentSchedualTasks = coreCount;
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
            maxConcurrentSchedualTasks = Mathf.Max(minConcurrentNoiseTasks, 1);
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
            maxConcurrentSchedualTasks =
                Mathf.Max(minConcurrentNoiseTasks, maxConcurrentSchedualTasks - 1);

            lastDecisionString = "Worker Saturation";
            ChunkProfiler.AP_LastDecision = lastDecisionString;
        }

        // ------------------------------------------------------------
        // 8. Recovery Mode
        // ------------------------------------------------------------
        if (!stressed && goodPerfTimer > 1.0f)
        {
            maxConcurrentSchedualTasks =
                Mathf.Min(maxConcurrentSchedualTasks + 1, coreCount);

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
        InjectChunkRequestIntoPipeline(playerPos);
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
        if (biomeDataManager == null)
        {
            Debug.LogError("BiomeDataManager not assigned!");
            return;
        }
        biomeDataManager.Initialize();

        if (biomeSystem == null)
        {
            Debug.LogError("biomeSystem not assigned!");
            return;
        }
        biomeSystem.SetSeed(seed);
        biomeSystem.biomeDataManager = biomeDataManager;
        biomeSystem.OnBiomeCompleted += HandleBiomeCompleted;

        // NoiseSystem set-up
        noiseSystem = new NoiseSystem();
        noiseSystem.Initialize(
            new NoiseSystem.NoiseConfig
            {
                seed = seed,
                frequency = frequency,
                chunkSize = chunkSize,
                maxConcurrentNoiseTasks =  maxConcurrentSchedualTasks,
                biomeDataManager = biomeDataManager
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
                GetSampleRes = lod => lodConfigs[lod].sampleRes,
                biomeDataManager = biomeDataManager
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
            GetBootstrapMode = () => bootstrapMode,
            biomeDataManager = biomeDataManager
        });
        decorationSystem.OnDecorationStarted += HandleDecorationStarted;
        decorationSystem.OnDecorationCompleted += HandleDecorationCompleted;

        // PendingWriteSystem set-up
        writeSystem = new WriteSystem();
        writeSystem.Initialize(
            chunkSize,
            coord => chunksStillDecorating.Contains(coord),
            coord => packSystem.IsPackInProgress(coord),
            coord => fullResBlocksByChunk.ContainsKey(coord) && fullResBlocksByChunk[coord].IsCreated,
            coord => fullResBlocksByChunk[coord],
            coord => MarkChunkForRemesh(coord),
            coord => GetDistanceAtChunkScaleWithRenderShape(coord, lastPlayerPos),    // getDistance()
            viewDistance,                                                             // forceGenRange
            EnqueueFrontier                                                           // queueFrontier()
        );

        // VoxelPackSystem set-up (block arrays -> GPU brick format)
        packSystem = new VoxelPackSystem();
        packSystem.Initialize(
            new VoxelPackSystem.Config
            {
                chunkSize = chunkSize,
                blockDb = nativeBlockDatabase,
                GetMaxUploadsPerFrame = () => Math.Max(1, (renderSettings != null ? renderSettings.uploadsPerFrame : 8) * Math.Min(currentMeshUploadsPerFrame, maxMeshUploadsPerFrame) / Math.Max(1, maxMeshUploadsPerFrame))
            },
            canPackNow: coord => CanMeshNow(coord),
            markPacked: coord => lastMeshFrame[coord] = Time.frameCount
        );
        packSystem.OnChunkPacked += HandleChunkPacked;
    }

    // =============================
    // ====== System Handles =======
    // =============================
    private void CompleteGeneration(int3 coord)
    {
        generatingSet.Remove(coord);
    }
    private void HandleBiomeCompleted(int3 coord, BiomeData biome)
    {
        biomeDataByChunk[coord] = biome;
        pendingBiome.Remove(coord);
        LODLevel lod = GetLODForCoord(coord, lastPlayerPos, LODLevel.Mid);
        noiseSystem.RequestDensity(coord, lod, biomeDataByChunk[coord]);
    }
    private void HandleECSSpawns(int3 coord, VoxelPackResult result)
    {
        if (result.lod != LODLevel.Near) return;
        if (!result.ecsSpawns.IsCreated || result.ecsSpawns.Length == 0) return;

        float3 worldOffset = (float3)ChunkToWorld(coord);

        // Destroy previous ECS decorations for this chunk (repack case)
        ECSSpawnSystem.DestroyChunk(new int3(coord.x, coord.y, coord.z));

        // RequestSpawn copies data into a blob - ecsSpawns stays owned by the pack result pool
        ECSSpawnSystem.RequestSpawn(
            new int3(coord.x, coord.y, coord.z),
            worldOffset,
            result.ecsSpawns.AsArray()
        );
    }
    private void HandleChunkPacked(int3 coord, VoxelPackResult result)
    {
        bool hadLod = lodByCoord.TryGetValue(coord, out var previousLod);
        lodByCoord[coord] = result.lod;
        if (hadLod && previousLod != result.lod)
            OnChunkLodChanged?.Invoke(coord, previousLod, result.lod);

        if (result.IsEmpty)
        {
            // Nothing visible: keep a null entry so the chunk still counts as generated
            voxelRenderer.SetChunkEmpty(coord);
            chunks[coord] = null;
        }
        else
        {
            if (!chunks.TryGetValue(coord, out var chunk) || chunk == null)
            {
                chunk = new Chunk();
                chunks[coord] = chunk;
            }
            chunk.lod = result.lod;

            Profiler.StartUpload();
            voxelRenderer.SetChunk(coord, result);
            Profiler.EndUpload();

            HandleECSSpawns(coord, result);
        }

        generatingSet.Remove(coord);
        deferredFrontier.Remove(coord);
    }
    private void HandleDecorationCompleted(int3 coord, NativeList<PendingBlockWrite> writes)
    {
        chunksStillDecorating.Remove(coord);

        if (!fullResBlocksByChunk.TryGetValue(coord, out var blockIds) || !blockIds.IsCreated)
        {
            Debug.LogError($"[Decoration] Completed for {coord} but no fullResBlocksByChunk exists."); // This error never occurs

            writeSystem.EnqueueDecorationWrites(coord, writes);

            // we still own the native list: dispose it
            if (writes.IsCreated)
                writes.Dispose();

            return;
        }

        // Enqueue all writes to WriteSystem
        writeSystem.EnqueueDecorationWrites(coord, writes);

        // Done with native container
        if (writes.IsCreated)
            writes.Dispose();

        // Done with the biome data
        if (biomeDataByChunk.TryGetValue(coord, out var biome))
        {
            if (biome.grid.IsCreated)
                biome.grid.Dispose();

            biomeDataByChunk.Remove(coord);
        }
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

            // A chunk regenerating after an unload gets back the blocks its loaded neighbours'
            // decorations put in it; they wait until its own decoration has run.
            writeSystem.ReplayStoredWrites(coord);

            // Pass BiomeData to decoration system
            if (!biomeDataByChunk.TryGetValue(coord, out var biomeData))
            {
                Debug.LogError($"[BlockGen] No biome data for {coord} when scheduling decoration!");
                return;
            }
            // Run decoration on full-res chunks only
            decorationSystem.ScheduleDecoration(coord, lod, blockIds, biomeData);
            return;
        }

        // --- Test for skipping Decoration ---
        {
            var faces = DetectTerrainFlowFromBlocks(blockIds, lod);
            ExpandFrontierFromFaces(coord, lastPlayerPos, faces);

            packSystem.RequestPack(coord, blockIds, lod);
        }
    }
    private void HandleDensityReady(int3 coord, LODLevel lod, NativeArray<float> density)
    {
        completedNoiseQueue.Enqueue((coord, density, lod));
    }

    // =============================
    // ===== VOXEL RENDER/PHYSICS ==
    // =============================
    void InitializeVoxelRenderer()
    {
        if (renderSettings == null)
            renderSettings = VoxelRenderSettings.FindOrCreate();
        renderSettings.Apply();

        var opaque = renderSettings.OpaqueMaterialOrDefault;
        var translucent = renderSettings.TranslucentMaterialOrDefault;
        if (opaque == null || translucent == null)
            Debug.LogError("[ChunkManager] Voxel raymarch materials are missing (Assets/Resources/VoxelRaymarch*.mat).");

        var blockTextures = textureArrayBuilder != null ? textureArrayBuilder.GetTextureArray() : null;

        voxelRenderer = new VoxelWorldRenderer(new VoxelWorldRenderer.Settings
        {
            chunkSize = chunkSize,
            initialChunkCapacity = renderSettings.initialChunkCapacity,
            initialPageCapacity = renderSettings.initialPageCapacity,
            opaqueMaterial = opaque,
            translucentMaterial = translucent,
            blockTextures = blockTextures,
            blockHeights = textureArrayBuilder != null ? textureArrayBuilder.generatedHeights : null,
            blockDb = nativeBlockDatabase,
            layer = gameObject.layer,
            gridDims = VoxelGridDims()
        });
    }

    void LateUpdate()
    {
        if (voxelRenderer == null)
            return;

        // Live settings
        voxelRenderer.castShadows = renderSettings.castIntoShadowMap;
        voxelRenderer.shadowMapDistance = renderSettings.shadowMapDistance;
        voxelRenderer.SetLodDistances(nearRange * chunkSize, midRange * chunkSize);
        voxelRenderer.FlushUploads();
    }

    // =============================
    // ============ LOD ============
    // =============================

    /// <summary>Raised when a loaded chunk changes LOD: (coord, previous, current).</summary>
    public event Action<int3, LODLevel, LODLevel> OnChunkLodChanged;

    /// <summary>Current LOD of a loaded (or generating) chunk.</summary>
    public bool TryGetChunkLod(int3 coord, out LODLevel lod) => lodByCoord.TryGetValue(coord, out lod);

    /// <summary>LOD at a world position: the chunk's current LOD, or what it would get from its distance.</summary>
    public LODLevel GetLodAt(Vector3 worldPosition)
    {
        int3 coord = WorldToChunkCoord(worldPosition);
        return TryGetChunkLod(coord, out var lod) ? lod : GetLODForCoord(coord, lastPlayerPos, LODLevel.Mid);
    }

    public LODSettings GetLodSettings(LODLevel lod) => lodConfigs[lod];

    void ApplyLodChange(int3 coord, LODLevel lod)
    {
        LODLevel previous = lodByCoord.TryGetValue(coord, out var stored) ? stored : lod;
        lodByCoord[coord] = lod;
        if (chunks.TryGetValue(coord, out var chunk) && chunk != null)
            chunk.lod = lod;

        // ECS decorations are only collected for Near chunks - a chunk coming into Near range
        // needs one pack pass to spawn them (cheap: block data is already in memory).
        if (lod == LODLevel.Near && previous != LODLevel.Near)
            MarkChunkForRemesh(coord);

        if (previous != lod)
            OnChunkLodChanged?.Invoke(coord, previous, lod);
    }

    // Toroidal chunk lookup grid used by GPU rays that cross chunk borders. Must be at least as
    // large as the loaded area, otherwise distant chunks alias onto the same cell.
    int3 VoxelGridDims()
    {
        int span = Mathf.NextPowerOfTwo(2 * (viewDistance + unloadPastViewDistance) + 3);
        return new int3(span, Mathf.Min(span, 64), span);
    }

    // IVoxelWorldSource - lets VoxelPhysics read block data directly.
    int IVoxelWorldSource.ChunkSize => chunkSize;
    NativeArray<BlockDatabase.BlockInfoUnmanaged> IVoxelWorldSource.BlockDatabase => nativeBlockDatabase;

    bool IVoxelWorldSource.TryGetChunkBlocks(int3 coord, out NativeArray<byte> blocks)
    {
        // Chunks being decorated are written by a job - not safe to read on the main thread.
        if (chunksStillDecorating.Contains(coord) ||
            !fullResBlocksByChunk.TryGetValue(coord, out blocks) || !blocks.IsCreated)
        {
            blocks = default;
            return false;
        }
        return true;
    }

    // =============================
    // ===== HELPER FUNCTIONS ======
    // =============================
    void EnsureBiome(int3 coord)
    {
        if (biomeDataByChunk.ContainsKey(coord))
            return;

        if (pendingBiome.Contains(coord))
            return;

        biomeSystem.RequestBiome(coord, chunkSize);
        pendingBiome.Add(coord);
    }
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

    // temp buffer — allocated once at class scope
    private static readonly List<int3> tmpWriteCoords = new List<int3>(256);

    // Process block writes from the write system
    // This is where extant decoration changes get updated into their respective chunks
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

            // Process this snapshot only — re-enqueued writes go to a NEW list
            // A chunk that isn't ready keeps its batch; otherwise return the list to the pool
            if (writeSystem.ProcessBatch(coord, batch))
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

        //ECS Cleanup
        ECSSpawnSystem.DestroyChunk(new int3(coord.x, coord.y, coord.z));

        // Biome cleanup
        if (pendingBiome.Contains(coord))
        {
            biomeSystem.CancelBiome(coord);
            pendingBiome.Remove(coord);
        }

        if (biomeDataByChunk.TryGetValue(coord, out var biome))
        {
            if (biome.grid.IsCreated)
                biome.grid.Dispose();
            biomeDataByChunk.Remove(coord);
        }

        // Stop any pack job still reading this chunk's blocks before they are disposed
        packSystem.Cancel(coord);

        // Clear stored block data
        if (fullResBlocksByChunk.TryGetValue(coord, out var arr))
        {
            if (arr.IsCreated) arr.Dispose();
            fullResBlocksByChunk.Remove(coord);
        }

        // Drop writes waiting for this chunk and the cross-chunk writes it sent
        writeSystem.OnChunkUnloaded(coord);
        lodByCoord.Remove(coord);
        chunksNeedingRemesh.Remove(coord);

        voxelRenderer.RemoveChunk(coord);
        chunks.Remove(coord);
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

        // Pending pack jobs read the block arrays disposed below
        packSystem?.Dispose();
        voxelRenderer?.Dispose();
        if (ReferenceEquals(VoxelPhysics.Source, this))
            VoxelPhysics.Source = null;

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
