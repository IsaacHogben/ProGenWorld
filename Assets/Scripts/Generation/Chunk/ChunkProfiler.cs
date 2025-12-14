using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;


public class ChunkProfiler : MonoBehaviour
{
    [SerializeField] private ChunkManager chunkManager;

    public static int ActiveNoiseTasks;
    public static int ActiveBlockJobs;
    public static int ActiveDecoJobs;
    public static int ActiveMeshJobs;
    public static int PendingWriteChunks;
    public static int TotalChunks;

    [Header("Display")]
    public bool showOnScreen = true;
    public bool showMeshStats = true;
    public bool showSystemStats = true;
    public bool showMainThreadLoopTimes = true;
    public bool showOnMoveStats = true;
    public bool showFrameGraph = true;
    public float logInterval = 5f;

    public static float avgNoise;
    public static float avgBlock;
    public static float avgDeco;
    public static float avgMesh;
    public static float avgUpload;

    public static float avgRemesh;
    public static float avgLod;
    public static float avgSched;

    public static float avgMove0;
    public static float avgMove1;
    public static float avgMove2;
    public static float avgMove3;

    // ============================
    // Adaptive Performance Debug
    // ============================
    public static float AP_EmaFrameTime;
    public static float AP_EmaWorkerLoad;
    public static float AP_EmaGpuTime;

    public static float AP_LastDecisionTime;
    public static string AP_LastDecision = "—";

    public static float AP_TargetFrameTime = 16.6f;  // 60 FPS
    public static float AP_WarnThreshold = 22f;      // mild stress
    public static float AP_CriticalThreshold = 30f;  // severe stress

    public static bool AP_IsCPUStressed;
    public static bool AP_IsGPUStressed;
    public static bool AP_IsWorkerStressed;

    // Window settings
    Rect mainRect = new Rect(10, 10, 250, 1400);
    Rect apRect = new Rect(270, 10, 300, 800);
    
    private GUIStyle label;
    GUIStyle adaptiveLabelStyle;


    void Awake()
    {
        label = new GUIStyle();
        label.fontSize = 14;
        label.richText = true;
        label.normal.textColor = Color.white;

        adaptiveLabelStyle = new GUIStyle();
        label.fontSize = 14;
        label.richText = true;
        label.normal.textColor = Color.white;
    }

    void Update()
    {
        if (!showOnScreen) return;
        MergeThreadedQueues();
        avgNoise = Avg(Profiler.noiseTimes, Profiler.noiseLock);
        avgBlock = Avg(Profiler.blockTimes, Profiler.blockLock);
        avgDeco = Avg(Profiler.decoTimes, Profiler.decoLock);
        avgMesh = Avg(Profiler.meshTimes, Profiler.meshLock);
        avgUpload = Avg(Profiler.uploadTimes, Profiler.uploadLock);

        avgRemesh = Avg(Profiler.remeshLoopTimes, Profiler.remeshLoopLock);
        avgLod = Avg(Profiler.lodLoopTimes, Profiler.lodLoopLock);
        avgSched = Avg(Profiler.schedLoopTimes, Profiler.schedLoopLock);

        avgMove0 = Avg(Profiler.movePhase0Times, Profiler.movePhase0Lock);
        avgMove1 = Avg(Profiler.movePhase1Times, Profiler.movePhase1Lock);
        avgMove2 = Avg(Profiler.movePhase2Times, Profiler.movePhase2Lock);
        avgMove3 = Avg(Profiler.movePhase3Times, Profiler.movePhase3Lock);

    }

    void OnGUI()
    {
        if (!showOnScreen) return;

        // Auto-size Main Window height
        float mainHeight = 10 +
                           (showMeshStats ? 500 : 260) +
                           (showSystemStats ? 320 : 0) +
                           (showMainThreadLoopTimes ? 320 : 0) +
                           (showOnMoveStats ? 320 : 0);

        mainRect.height = mainHeight;

        // Draw Main Window
        GUILayout.BeginArea(mainRect, "Chunk Profiler", GUI.skin.window);
        DrawMainWindowContents();
        GUILayout.EndArea();

        // Position AP window to the right of Main Window
        apRect.x = mainRect.x + mainRect.width + 10;
        apRect.y = mainRect.y;
        apRect.height = 420;

        GUILayout.BeginArea(apRect, "Adaptive Performance", GUI.skin.window);
        DrawAdaptivePerformanceContents();
        GUILayout.EndArea();
    }

    void DrawMainWindowContents()
    {
        float total = avgNoise + avgBlock + avgDeco + avgMesh + avgUpload;

        float fps = 1f / Time.deltaTime;
        float ft = Time.deltaTime * 1000f;

        GUILayout.Label("<b>Frame</b>", label);
        GUILayout.Label($"FPS: {fps:F1}");
        GUILayout.Label($"Frame Time: {ft:F2} ms");
        GUILayout.Space(6);

        GUILayout.Label("<b>Chunk Generation Costs</b>", label);
        GUILayout.Label($"Noise:      {avgNoise:F2} ms");
        GUILayout.Label($"BlockGen:   {avgBlock:F2} ms");
        GUILayout.Label($"Decorate:   {avgDeco:F2} ms");
        GUILayout.Label($"Mesh Job:   {avgMesh:F2} ms");
        GUILayout.Label($"Upload:     {avgUpload:F2} ms");
        GUILayout.Space(2);
        GUILayout.Label($"<b>Total:     {total:F2} ms</b>", label);

        GUILayout.Space(10);

        if (chunkManager != null)
        {
            GUILayout.Label("<b>Adaptive System</b>", label);
            GUILayout.Label($"Avg Frame Time: {chunkManager.AvgFrameTime:F1} ms");
            GUILayout.Label($"Noise Tasks:    {chunkManager.CurrentNoiseTasks}");
            GUILayout.Label($"Mesh Uploads:   {chunkManager.CurrentMeshUploads}");
            GUILayout.Label($"Schedule Int:   {chunkManager.CurrentScheduleInterval:F2}s");
            GUILayout.Label($"Pool Size:      {chunkManager.CurrentPoolSize}");
            GUILayout.Space(10);
        }

        if (showMainThreadLoopTimes)
        {
            GUILayout.Label("<b>Main Thread Loop Costs</b>", label);
            GUILayout.Label($"Remesh Loop:  {avgRemesh:F3} ms");
            GUILayout.Label($"LOD Loop:     {avgLod:F3} ms");
            GUILayout.Label($"Sched Loop:   {avgSched:F3} ms");
            GUILayout.Space(10);
        }

        if (showSystemStats)
        {
            GUILayout.Label("<b>System Stats</b>", label);
            GUILayout.Label($"Noise Tasks:         {ActiveNoiseTasks}");
            GUILayout.Label($"BlockGen Jobs:       {ActiveBlockJobs}");
            GUILayout.Label($"Decoration Jobs:     {ActiveDecoJobs}");
            GUILayout.Label($"Mesh Jobs:           {ActiveMeshJobs}");
            GUILayout.Label($"Pending Write Chunks:{PendingWriteChunks}");
            GUILayout.Label($"Active Chunks:       {TotalChunks}");
            GUILayout.Label($"CPU Threads:         {SystemInfo.processorCount}");
            GUILayout.Label($"Managed Threads:     {Process.GetCurrentProcess().Threads.Count}");
            GUILayout.Space(10);
        }

        if (showMeshStats)
        {
            GUILayout.Label("<b>Mesh Stats</b>", label);

            int poolCount = chunkManager?.meshPool?.Count ?? 0;
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            int activeMeshes = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None)
                .Count(mf => mf.sharedMesh != null);
            int vertTotal = meshes.Sum(m => m.vertexCount);

            GUILayout.Label($"Active Meshes:  {activeMeshes}");
            GUILayout.Label($"Mesh Pool:      {poolCount}");
            GUILayout.Label($"All Meshes:     {meshes.Length}");
            GUILayout.Label($"Total Vertices: {vertTotal:N0}");
        }

        if (showOnMoveStats)
        {
            GUILayout.Space(10);
            GUILayout.Label("<b>Movement Phase Costs</b>", label);
            GUILayout.Label($"Phase 0 (Cleanup + Promote): {avgMove0:F3} ms");
            GUILayout.Label($"Phase 1 (LOD Updates):        {avgMove1:F3} ms");
            GUILayout.Label($"Phase 2 (Unload Chunks):      {avgMove2:F3} ms");
            GUILayout.Label($"Phase 3 (Cull Block Jobs):    {avgMove3:F3} ms");

        }
    }
    void DrawLoadBarWindow()
    {
        GUILayout.BeginArea(new Rect(400, 10, 360, 160), "Load Bars", GUI.skin.window);
        GUILayout.Space(4);

        DrawLoadBar(avgNoise, avgBlock, avgDeco, avgMesh, avgUpload);

        GUILayout.EndArea();
    }
    void DrawAdaptivePerformanceContents()
    {
        InitApStyles();

        GUILayout.Label($"Frame Time EMA: {AP_EmaFrameTime:F2} ms", apLabel);
        GUILayout.Label($"Worker Load EMA: {AP_EmaWorkerLoad:P0}", apLabel);
        if (AP_EmaGpuTime > 0)
            GUILayout.Label($"GPU Time EMA: {AP_EmaGpuTime:F2} ms", apLabel);

        GUILayout.Space(6);

        float barW = 240f;
        float barH = 12f;

        GUILayout.Label("CPU Pressure", apBold);
        Rect cpuRect = GUILayoutUtility.GetRect(barW, barH);
        DrawLoadBar(cpuRect.x, cpuRect.y, barW, barH,
            Mathf.InverseLerp(0, AP_WarnThreshold * 1.2f, AP_EmaFrameTime),
            AP_IsCPUStressed ? Color.red : Color.green);

        GUILayout.Label("GPU Pressure", apBold);
        Rect gpuRect = GUILayoutUtility.GetRect(barW, barH);
        DrawLoadBar(gpuRect.x, gpuRect.y, barW, barH,
            Mathf.InverseLerp(0, AP_WarnThreshold * 1.2f, AP_EmaGpuTime),
            AP_IsGPUStressed ? Color.red : Color.green);

        GUILayout.Label("Worker Load", apBold);
        Rect wRect = GUILayoutUtility.GetRect(barW, barH);
        DrawLoadBar(wRect.x, wRect.y, barW, barH,
            AP_EmaWorkerLoad,
            AP_IsWorkerStressed ? new Color(1f, 0.6f, 0f) : Color.green);

        GUILayout.Space(6);

        GUILayout.Label("<b>Last Decision:</b>", apBold);
        GUILayout.Label($"{AP_LastDecision}", apLabel);
        GUILayout.Label($"At: {AP_LastDecisionTime:F1}s", apLabel);

        GUILayout.Space(6);
        GUILayout.Label("<b>Throttle State</b>", apBold);

        GUILayout.Label($"Mesh Uploads / Frame: {chunkManager.currentMeshUploadsPerFrame}", apLabel);
        GUILayout.Label($"Noise Concurrency: {chunkManager.maxConcurrentSchedualTasks}", apLabel);
        GUILayout.Label($"Schedule Interval: {chunkManager.scheduleInterval:F3}s", apLabel);
        GUILayout.Label($"Mesh Debounce: {chunkManager.meshDebounceFrames}", apLabel);
    }

    // ------------------------------------------------------------
    // Adaptive Perf UI — internal styles (safe, no skin required)
    // ------------------------------------------------------------
    GUIStyle apHeader;
    GUIStyle apLabel;
    GUIStyle apBold;

    void InitApStyles()
    {
        if (apHeader != null) return;

        apHeader = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        apBold = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        apLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };
    }

    // Simple load bar (color-coded)
    void DrawLoadBar(float x, float y, float width, float height, float t, Color c)
    {
        Rect rBg = new Rect(x, y, width, height);
        Rect rFg = new Rect(x, y, width * Mathf.Clamp01(t), height);

        // Background
        Color prev = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.6f);
        GUI.DrawTexture(rBg, Texture2D.whiteTexture);

        // Foreground
        GUI.color = c;
        GUI.DrawTexture(rFg, Texture2D.whiteTexture);

        GUI.color = prev;
    }

    // ===========================================
    // MERGING THREAD-SAFE TIMINGS
    // ===========================================

    void MergeThreadedQueues()
    {
        TrimQueue(Profiler.noiseLock, Profiler.noiseTimes);
        TrimQueue(Profiler.blockLock, Profiler.blockTimes);
        TrimQueue(Profiler.decoLock, Profiler.decoTimes);
        TrimQueue(Profiler.meshLock, Profiler.meshTimes);
        TrimQueue(Profiler.uploadLock, Profiler.uploadTimes);
    }

    void TrimQueue(object lockObj, Queue<float> q)
    {
        lock (lockObj)
        {
            while (q.Count > 300)
                q.Dequeue();
        }
    }

    // ===========================================
    // HELPERS
    // ===========================================
    void DrawLoadBar(float noise, float block, float deco, float mesh, float upload)
    {
        float max = Mathf.Max(0.01f,
            Mathf.Max(noise, Mathf.Max(block, Mathf.Max(deco, Mathf.Max(mesh, upload))))
        );

        float Bar(string label, float value, Color color)
        {
            float width = (value / max) * 300f;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(80));

            var rect = GUILayoutUtility.GetRect(300, 16);

            Color prev = GUI.color;
            GUI.color = color;
            GUI.Box(new Rect(rect.x, rect.y, width, rect.height), GUIContent.none);
            GUI.color = prev;

            GUILayout.EndHorizontal();

            return width;
        }

        Bar("Noise", noise, new Color(0.3f, 0.7f, 1f));
        Bar("Block", block, new Color(0.2f, 1f, 0.4f));
        Bar("Deco", deco, new Color(1f, 0.7f, 0.2f));
        Bar("Mesh", mesh, new Color(1f, 0.3f, 0.3f));
        Bar("Upload", upload, new Color(0.8f, 0.4f, 1f));
    }
    float Avg(Queue<float> q, object lockObj)
    {
        lock (lockObj)
        {
            float sum = 0f;
            int count = q.Count;
            if (count == 0) return 0f;

            foreach (var v in q)
                sum += v;

            return sum / count;
        }
    }
}

public static class ChunkProfilerTimes
{
    public static readonly Queue<float> noiseDurations = new();
    public static readonly object noiseLock = new();
}
public static class Profiler
{
    public static readonly object noiseLock = new();
    public static readonly object blockLock = new();
    public static readonly object decoLock = new();
    public static readonly object meshLock = new();
    public static readonly object uploadLock = new();

    public static readonly Queue<float> noiseTimes = new();
    public static readonly Queue<float> blockTimes = new();
    public static readonly Queue<float> decoTimes = new();
    public static readonly Queue<float> meshTimes = new();
    public static readonly Queue<float> uploadTimes = new();

    [ThreadStatic] private static Stopwatch threadWatch;
    public static readonly object movePhase0Lock = new();
    public static readonly object movePhase1Lock = new();
    public static readonly object movePhase2Lock = new();
    public static readonly object movePhase3Lock = new();

    public static readonly Queue<float> movePhase0Times = new();
    public static readonly Queue<float> movePhase1Times = new();
    public static readonly Queue<float> movePhase2Times = new();
    public static readonly Queue<float> movePhase3Times = new();

    [ThreadStatic] static Stopwatch moveWatch;
    static Stopwatch Wm()
    {
        if (moveWatch == null) moveWatch = new Stopwatch();
        return moveWatch;
    }

    public static void StartMovePhase() => Wm().Restart();

    public static void EndMovePhase0()
    {
        lock (movePhase0Lock)
            movePhase0Times.Enqueue((float)Wm().Elapsed.TotalMilliseconds);
    }

    public static void EndMovePhase1()
    {
        lock (movePhase1Lock)
            movePhase1Times.Enqueue((float)Wm().Elapsed.TotalMilliseconds);
    }

    public static void EndMovePhase2()
    {
        lock (movePhase2Lock)
            movePhase2Times.Enqueue((float)Wm().Elapsed.TotalMilliseconds);
    }

    public static void EndMovePhase3()
    {
        lock (movePhase3Lock)
            movePhase3Times.Enqueue((float)Wm().Elapsed.TotalMilliseconds);
    }

    private static Stopwatch GetWatch()
    {
        if (threadWatch == null)
            threadWatch = new Stopwatch();
        return threadWatch;
    }

    public static void StartNoise() => GetWatch().Restart();
    public static void StartBlock() => GetWatch().Restart();
    public static void StartDeco() => GetWatch().Restart();
    public static void StartMesh() => GetWatch().Restart();
    public static void StartUpload() => GetWatch().Restart();

    public static void EndNoise() { lock (noiseLock) noiseTimes.Enqueue((float)GetWatch().Elapsed.TotalMilliseconds); }
    public static void EndBlock() { lock (blockLock) blockTimes.Enqueue((float)GetWatch().Elapsed.TotalMilliseconds); }
    public static void EndDeco() { lock (decoLock) decoTimes.Enqueue((float)GetWatch().Elapsed.TotalMilliseconds); }
    public static void EndMesh() { lock (meshLock) meshTimes.Enqueue((float)GetWatch().Elapsed.TotalMilliseconds); }
    public static void EndUpload() { lock (uploadLock) uploadTimes.Enqueue((float)GetWatch().Elapsed.TotalMilliseconds); }

    // MAIN THREAD
    public static readonly object remeshLoopLock = new();
    public static readonly object lodLoopLock = new();
    public static readonly object schedLoopLock = new();

    public static readonly Queue<float> remeshLoopTimes = new();
    public static readonly Queue<float> lodLoopTimes = new();
    public static readonly Queue<float> schedLoopTimes = new();

    [ThreadStatic] static Stopwatch remeshWatch;
    [ThreadStatic] static Stopwatch lodWatch;
    [ThreadStatic] static Stopwatch schedWatch;

    static Stopwatch W(ref Stopwatch sw)
    {
        if (sw == null) sw = new Stopwatch();
        return sw;
    }

    // Loop profiling
    public static void StartRemeshLoop() => W(ref remeshWatch).Restart();
    public static void EndRemeshLoop()
    {
        lock (remeshLoopLock)
            remeshLoopTimes.Enqueue((float)remeshWatch.Elapsed.TotalMilliseconds);
    }

    public static void StartLodLoop() => W(ref lodWatch).Restart();
    public static void EndLodLoop()
    {
        lock (lodLoopLock)
            lodLoopTimes.Enqueue((float)lodWatch.Elapsed.TotalMilliseconds);
    }

    public static void StartSchedLoop() => W(ref schedWatch).Restart();
    public static void EndSchedLoop()
    {
        lock (schedLoopLock)
            schedLoopTimes.Enqueue((float)schedWatch.Elapsed.TotalMilliseconds);
    }
}


