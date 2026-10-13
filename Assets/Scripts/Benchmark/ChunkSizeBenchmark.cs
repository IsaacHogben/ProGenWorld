// ChunkSizeBenchmark.cs
// Command-line driven benchmark for comparing chunk sizes in a standalone build.
// Inactive unless the player is started with -benchChunkSize <n>.
//
// View distance, LOD ranges and the unload margin are measured in chunks, so they are rescaled
// from the scene values to keep the same world-space distances at every chunk size.
//
// Phases: Load (until "Initial Loading Complete!") -> Idle (stationary) -> Fly (straight line at a
// fixed speed) -> CatchUp (stationary until the pipeline drains). Results are written as JSON.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;

[DefaultExecutionOrder(-10000)]
public class ChunkSizeBenchmark : MonoBehaviour
{
    int chunkSize;
    string outPath;
    float idleSeconds = 15f;
    float flySeconds = 45f;
    float flySpeed = 48f;
    float loadTimeout = 300f;
    float catchUpTimeout = 180f;

    ChunkManager chunkManager;
    Transform player;
    bool loadComplete;
    float loadCompleteTime = -1f;

    ProfilerRecorder totalUsedMemory;
    ProfilerRecorder gcUsedMemory;

    FieldInfo chunksField, generatingField, frontierField, lodUpdateField;
    FieldInfo noiseSystemField, packSystemField, decoSystemField, blockGenSystemField;

    readonly StringBuilder json = new();
    readonly List<string> log = new();

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static float ArgFloat(string name, float fallback)
        => float.TryParse(Arg(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    void Awake()
    {
        if (!int.TryParse(Arg("-benchChunkSize"), out chunkSize))
        {
            Destroy(this);
            return;
        }

        outPath = Arg("-benchOut") ?? Path.Combine(Application.persistentDataPath, $"chunkbench_{chunkSize}.json");
        idleSeconds = ArgFloat("-benchIdle", idleSeconds);
        flySeconds = ArgFloat("-benchFly", flySeconds);
        flySpeed = ArgFloat("-benchSpeed", flySpeed);

        chunkManager = FindFirstObjectByType<ChunkManager>();
        var view = FindFirstObjectByType<ViewDistanceSettings>();
        var render = FindFirstObjectByType<VoxelRenderSettings>();

        // Reference world distances come from the scene as authored.
        int refSize = chunkManager.chunkSize;
        float k = refSize / (float)chunkSize;
        int refView = view.viewDistance, refUnload = view.unloadPastViewDistance;
        int refNear = view.nearRange, refMid = view.midRange;

        chunkManager.chunkSize = chunkSize;
        view.viewDistance = Mathf.Max(1, Mathf.RoundToInt(refView * k));
        view.unloadPastViewDistance = Mathf.Max(1, Mathf.RoundToInt(refUnload * k));
        view.nearRange = Mathf.Max(1, Mathf.RoundToInt(refNear * k));
        view.midRange = Mathf.Max(view.nearRange, Mathf.RoundToInt(refMid * k));
        if (render != null)
        {
            render.initialChunkCapacity = Mathf.Max(64, Mathf.RoundToInt(render.initialChunkCapacity * k * k));
            if (int.TryParse(Arg("-benchChunkCapacity"), out int chunkCap)) render.initialChunkCapacity = chunkCap;
            if (int.TryParse(Arg("-benchPageCapacity"), out int pageCap)) render.initialPageCapacity = pageCap;
            Log($"gpu capacity: chunks {render.initialChunkCapacity} pages {render.initialPageCapacity}");
        }
        if (int.TryParse(Arg("-benchNoiseThreads"), out int noiseThreads))
            chunkManager.noiseThreads = noiseThreads;
        Log($"noise threads {chunkManager.NoiseThreadCount}");
        if (int.TryParse(Arg("-benchBiomePerFrame"), out int biomePerFrame))
            chunkManager.biomeSystem.maxRequestsPerFrame = biomePerFrame;
        Log($"biome requests per frame {chunkManager.biomeSystem.maxRequestsPerFrame}");
        chunkManager.decorationBudgetMs = ArgFloat("-benchDecoBudget", chunkManager.decorationBudgetMs);
        Log($"decoration budget {chunkManager.decorationBudgetMs} ms");

        if (int.TryParse(Arg("-benchView"), out int benchView))
            view.viewDistance = benchView;
        if (Enum.TryParse(Arg("-benchBlockStorage") ?? "", true, out BlockStorage storage))
            view.midBlockStorage = view.farBlockStorage = storage;
        if (int.TryParse(Arg("-benchTrimGpu"), out int trim))
            view.trimGpuDetail = trim != 0;
        if (int.TryParse(Arg("-benchLowestDetail"), out int lowest))
            view.lowestDetailRange = lowest;
        if (int.TryParse(Arg("-benchHorizon"), out int horizon))
            view.lowResHorizon = horizon != 0;
        Log($"block storage mid {view.midBlockStorage} far {view.farBlockStorage}, trim gpu {view.trimGpuDetail}, lowest detail {view.lowestDetailRange}, low res horizon {view.lowResHorizon}");

        Log($"chunkSize {refSize} -> {chunkSize}  view {refView}->{view.viewDistance}  near {refNear}->{view.nearRange}  mid {refMid}->{view.midRange}  unloadPast {refUnload}->{view.unloadPastViewDistance}");
        Log($"world: view {view.viewDistance * chunkSize}u near {view.nearRange * chunkSize}u mid {view.midRange * chunkSize}u");

        // The on-screen profiler draws a lot of IMGUI every frame; keep it out of the measurements.
        foreach (var p in FindObjectsByType<ChunkProfiler>(FindObjectsSortMode.None))
            p.enabled = false;

        // Take the player out of physics / input so the benchmark controls its position.
        player = chunkManager.player.transform;
        foreach (var c in player.GetComponents<Component>())
        {
            string n = c.GetType().Name;
            Log($"player component: {n}");
            if (c is CharacterController cc) cc.enabled = false;
            else if (c is Rigidbody rb) rb.isKinematic = true;
            else if (c is Behaviour b && (n.Contains("Controller") || n.Contains("Motor") || n.Contains("Body") || n.Contains("Ragdoll")))
                b.enabled = false;
        }

        var t = typeof(ChunkManager);
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        chunksField = t.GetField("chunks", F);
        generatingField = t.GetField("generatingSet", F);
        frontierField = t.GetField("frontier", F);
        lodUpdateField = t.GetField("lodUpdateQueue", F);
        noiseSystemField = t.GetField("noiseSystem", F);
        packSystemField = t.GetField("packSystem", F);
        decoSystemField = t.GetField("decorationSystem", F);
        blockGenSystemField = t.GetField("blockGenSystem", F);

        totalUsedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
        gcUsedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");

        Application.logMessageReceived += OnLog;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        if (totalUsedMemory.Valid) totalUsedMemory.Dispose();
        if (gcUsedMemory.Valid) gcUsedMemory.Dispose();
    }

    void OnLog(string msg, string stack, LogType type)
    {
        if (!loadComplete && msg.StartsWith("Initial Loading Complete"))
        {
            loadComplete = true;
            loadCompleteTime = Time.realtimeSinceStartup;
        }
        if (type == LogType.Exception || type == LogType.Error)
            Log($"[{type}] {msg}");
    }

    void Log(string s)
    {
        log.Add(s);
        Debug.Log("[ChunkBench] " + s);
    }

    IEnumerator Start()
    {
        float startTime = Time.realtimeSinceStartup;
        Vector3 startPos = player.position;

        json.Append("{\n");
        json.Append($"  \"chunkSize\": {chunkSize},\n");
        json.Append($"  \"viewDistanceChunks\": {chunkManager.viewDistance},\n");

        // --- Load ---
        var load = new FrameStats();
        while (!loadComplete && Time.realtimeSinceStartup - startTime < loadTimeout)
        {
            yield return null;
            load.Add();
        }
        float loadSeconds = (loadComplete ? loadCompleteTime : Time.realtimeSinceStartup) - startTime;
        Log($"load {(loadComplete ? "complete" : "TIMED OUT")} in {loadSeconds:F2}s, chunks {Count(chunksField)}");
        json.Append($"  \"loadCompleted\": {(loadComplete ? "true" : "false")},\n");
        json.Append($"  \"loadSeconds\": {F(loadSeconds)},\n");
        json.Append($"  \"load\": {load.ToJson()},\n");
        json.Append($"  \"loadStages\": {DrainStages()},\n");
        json.Append($"  \"afterLoad\": {Snapshot()},\n");

        // --- Idle ---
        yield return Wait(2f);
        var idle = new FrameStats();
        float end = Time.realtimeSinceStartup + idleSeconds;
        while (Time.realtimeSinceStartup < end)
        {
            yield return null;
            idle.Add();
        }
        json.Append($"  \"idle\": {idle.ToJson()},\n");
        json.Append($"  \"afterIdle\": {Snapshot()},\n");
        DrainStages();

        // --- Fly ---
        var fly = new FrameStats();
        var backlog = new List<int>();
        var flyVisible = new List<int>();
        Vector3 dir = new Vector3(1f, 0f, 0.35f).normalized;
        player.rotation = Quaternion.LookRotation(dir, Vector3.up);
        float flyStart = Time.realtimeSinceStartup;
        float nextSample = 0f;
        while (Time.realtimeSinceStartup - flyStart < flySeconds)
        {
            float t = Time.realtimeSinceStartup - flyStart;
            player.position = startPos + dir * (flySpeed * t);
            yield return null;
            fly.Add();
            if (t >= nextSample)
            {
                nextSample += 0.5f;
                backlog.Add(Backlog());
                flyVisible.Add(VisibleCells());
            }
        }
        json.Append($"  \"fly\": {fly.ToJson()},\n");
        json.Append($"  \"flyBacklog\": {{ \"mean\": {F(Mean(backlog))}, \"max\": {Max(backlog)} }},\n");
        json.Append($"  \"flyVisibleCells\": [{string.Join(", ", flyVisible)}],\n");
        json.Append($"  \"flyStages\": {DrainStages()},\n");
        json.Append($"  \"afterFly\": {Snapshot()},\n");

        // --- CatchUp: stationary until nothing is queued or running ---
        var catchUp = new FrameStats();
        float cuStart = Time.realtimeSinceStartup;
        int quietFrames = 0;
        var cuVisible = new List<int>();
        float cuNext = 0f;
        while (Time.realtimeSinceStartup - cuStart < catchUpTimeout && quietFrames < 30)
        {
            yield return null;
            catchUp.Add();
            if (Time.realtimeSinceStartup - cuStart >= cuNext)
            {
                cuNext += 0.5f;
                cuVisible.Add(VisibleCells());
            }
            quietFrames = Backlog() == 0 && ActiveJobs() == 0 ? quietFrames + 1 : 0;
        }
        float catchUpSeconds = Time.realtimeSinceStartup - cuStart;
        json.Append($"  \"catchUpSeconds\": {F(catchUpSeconds)},\n");
        json.Append($"  \"catchUpDrained\": {(quietFrames >= 30 ? "true" : "false")},\n");
        json.Append($"  \"catchUp\": {catchUp.ToJson()},\n");
        json.Append($"  \"catchUpVisibleCells\": [{string.Join(", ", cuVisible)}],\n");
        json.Append($"  \"catchUpStages\": {DrainStages()},\n");
        json.Append($"  \"final\": {Snapshot()},\n");

        json.Append("  \"log\": [");
        for (int i = 0; i < log.Count; i++)
            json.Append((i > 0 ? ", " : "") + "\"" + log[i].Replace("\\", "/").Replace("\"", "'") + "\"");
        json.Append("]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        File.WriteAllText(outPath, json.ToString());
        Debug.Log($"[ChunkBench] Wrote {outPath}");
        Application.Quit();
    }

    static IEnumerator Wait(float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end) yield return null;
    }

    // Non-empty, non-buried 16^3 draw cells: rendered terrain surface, independent of chunk size.
    int VisibleCells() => chunkManager.VoxelRenderer.TotalCells - chunkManager.VoxelRenderer.BuriedCells;

    static string DrainStages()
    {
        return "{ " +
               $"\"biome\": {Drain(Profiler.biomeTimes, Profiler.biomeLock)}, " +
               $"\"noise\": {Drain(Profiler.noiseTimes, Profiler.noiseLock)}, " +
               $"\"block\": {Drain(Profiler.blockTimes, Profiler.blockLock)}, " +
               $"\"deco\": {Drain(Profiler.decoTimes, Profiler.decoLock)}, " +
               $"\"pack\": {Drain(Profiler.meshTimes, Profiler.meshLock)}, " +
               $"\"upload\": {Drain(Profiler.uploadTimes, Profiler.uploadLock)}, " +
               $"\"remeshLoop\": {Drain(Profiler.remeshLoopTimes, Profiler.remeshLoopLock)}, " +
               $"\"schedLoop\": {Drain(Profiler.schedLoopTimes, Profiler.schedLoopLock)}, " +
               $"\"lodLoop\": {Drain(Profiler.lodLoopTimes, Profiler.lodLoopLock)}, " +
               $"\"move0\": {Drain(Profiler.movePhase0Times, Profiler.movePhase0Lock)}, " +
               $"\"move1\": {Drain(Profiler.movePhase1Times, Profiler.movePhase1Lock)}, " +
               $"\"move2\": {Drain(Profiler.movePhase2Times, Profiler.movePhase2Lock)}, " +
               $"\"move3\": {Drain(Profiler.movePhase3Times, Profiler.movePhase3Lock)}" +
               " }";
    }

    static string Drain(Queue<float> q, object gate)
    {
        lock (gate)
        {
            double sum = 0, max = 0;
            int n = q.Count;
            while (q.Count > 0) { float v = q.Dequeue(); sum += v; max = Math.Max(max, v); }
            return $"{{ \"n\": {n}, \"mean\": {F(n > 0 ? sum / n : 0)}, \"max\": {F(max)}, \"total\": {F(sum)} }}";
        }
    }

    int Count(FieldInfo f) => f?.GetValue(chunkManager) is System.Collections.ICollection c ? c.Count : -1;

    int Backlog() => Math.Max(0, Count(frontierField)) + Math.Max(0, Count(generatingField)) + Math.Max(0, Count(lodUpdateField));

    int ActiveJobs()
    {
        int n = 0;
        n += ActiveOf(noiseSystemField, "ActiveTasks");
        n += ActiveOf(blockGenSystemField, "ActiveJobs");
        n += ActiveOf(decoSystemField, "ActiveJobs");
        n += ActiveOf(packSystemField, "ActiveJobs");
        return n;
    }

    int ActiveOf(FieldInfo f, string prop)
    {
        var sys = f?.GetValue(chunkManager);
        var p = sys?.GetType().GetProperty(prop);
        return p != null ? Convert.ToInt32(p.GetValue(sys)) : 0;
    }

    string Snapshot()
    {
        var r = chunkManager.VoxelRenderer;
        var store = chunkManager.BlockStore;
        return "{ " +
               $"\"chunks\": {Count(chunksField)}, " +
               $"\"blockArrays\": {store.FullCount}, " +
               $"\"blockArrayMB\": {F(store.FullBytes / 1048576.0)}, " +
               $"\"compressedBlocks\": {store.CompressedCount}, " +
               $"\"compressedMB\": {F(store.CompressedBytes / 1048576.0)}, " +
               $"\"horizonChunks\": {chunkManager.CountChunksAtLod(LODLevel.Horizon)}, " +
               $"\"gpuChunks\": {r.LoadedChunks}, " +
               $"\"gpuCells\": {r.TotalCells}, " +
               $"\"buriedCells\": {r.BuriedCells}, " +
               $"\"visibleCells\": {r.TotalCells - r.BuriedCells}, " +
               $"\"drawnCells\": {r.LastDrawnCells}, " +
               $"\"usedPages\": {r.UsedPages}, " +
               $"\"gpuMB\": {F(r.GpuBytes / 1048576.0)}, " +
               $"\"totalUsedMemMB\": {F(totalUsedMemory.LastValue / 1048576.0)}, " +
               $"\"gcUsedMemMB\": {F(gcUsedMemory.LastValue / 1048576.0)}, " +
               $"\"backlog\": {Backlog()}" +
               " }";
    }

    static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    static double Mean(List<int> l) { if (l.Count == 0) return 0; double s = 0; foreach (var v in l) s += v; return s / l.Count; }
    static int Max(List<int> l) { int m = 0; foreach (var v in l) m = Math.Max(m, v); return m; }

    /// <summary>Per-frame CPU (wall) and GPU frame times.</summary>
    class FrameStats
    {
        readonly List<float> cpu = new(4096);
        readonly List<float> gpu = new(4096);
        readonly FrameTiming[] timings = new FrameTiming[1];

        public void Add()
        {
            cpu.Add(Time.unscaledDeltaTime * 1000f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0)
                gpu.Add((float)timings[0].gpuFrameTime);
        }

        public string ToJson()
        {
            return "{ " +
                   $"\"frames\": {cpu.Count}, " +
                   $"\"cpu\": {Dist(cpu)}, " +
                   $"\"gpu\": {Dist(gpu)}, " +
                   $"\"hitches33\": {cpu.FindAll(v => v > 33.3f).Count}, " +
                   $"\"hitches50\": {cpu.FindAll(v => v > 50f).Count}, " +
                   $"\"hitches100\": {cpu.FindAll(v => v > 100f).Count}, " +
                   $"\"hitchFrames\": [{HitchFrames()}]" +
                   " }";
        }

        // "frameIndex:ms" for each frame over 50 ms, in phase order
        string HitchFrames()
        {
            var parts = new List<string>();
            for (int i = 0; i < cpu.Count; i++)
                if (cpu[i] > 50f) parts.Add($"\"{i}:{F(cpu[i])}\"");
            return string.Join(", ", parts);
        }

        static string Dist(List<float> l)
        {
            if (l.Count == 0) return "null";
            var s = new List<float>(l);
            s.Sort();
            double sum = 0;
            foreach (var v in s) sum += v;
            float P(float q) => s[Mathf.Clamp(Mathf.CeilToInt(q * s.Count) - 1, 0, s.Count - 1)];
            return "{ " +
                   $"\"mean\": {F(sum / s.Count)}, \"p50\": {F(P(0.5f))}, \"p95\": {F(P(0.95f))}, " +
                   $"\"p99\": {F(P(0.99f))}, \"max\": {F(s[s.Count - 1])}" +
                   " }";
        }
    }
}
