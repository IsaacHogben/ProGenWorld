using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    public float logInterval = 5f;

    private GUIStyle label;

    float lastLogTime;

    void Awake()
    {
        label = new GUIStyle();
        label.fontSize = 14;
        label.richText = true;
        label.normal.textColor = Color.white;
    }

    void Update()
    {
        MergeThreadedQueues();
    }

    void OnGUI()
    {
        if (!showOnScreen) return;
        DrawMainWindow();
        DrawLoadBarWindow();
    }
    void DrawMainWindow()
    {
        float avgNoise = Avg(Profiler.noiseTimes, Profiler.noiseLock);
        float avgBlock = Avg(Profiler.blockTimes, Profiler.blockLock);
        float avgDeco = Avg(Profiler.decoTimes, Profiler.decoLock);
        float avgMesh = Avg(Profiler.meshTimes, Profiler.meshLock);
        float avgUpload = Avg(Profiler.uploadTimes, Profiler.uploadLock);

        float avgRemesh = Avg(Profiler.remeshLoopTimes, Profiler.remeshLoopLock);
        float avgLod = Avg(Profiler.lodLoopTimes, Profiler.lodLoopLock);
        float avgSched = Avg(Profiler.schedLoopTimes, Profiler.schedLoopLock);

        float total = avgNoise + avgBlock + avgDeco + avgMesh + avgUpload;

        float fps = 1f / Time.deltaTime;
        float ft = Time.deltaTime * 1000f;

        GUILayout.BeginArea(new Rect(10, 10, 380, 900), "Chunk Profiler", GUI.skin.window);

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
        }

        if (showSystemStats)
        {
            GUILayout.Label("<b>System Stats</b>", label);

            GUILayout.Label($"Noise Tasks:         {ChunkProfiler.ActiveNoiseTasks}");
            GUILayout.Label($"BlockGen Jobs:       {ChunkProfiler.ActiveBlockJobs}");
            GUILayout.Label($"Decoration Jobs:     {ChunkProfiler.ActiveDecoJobs}");
            GUILayout.Label($"Mesh Jobs:           {ChunkProfiler.ActiveMeshJobs}");
            GUILayout.Label($"Pending Write Chunks:{ChunkProfiler.PendingWriteChunks}");
            GUILayout.Label($"Active Chunks:       {ChunkProfiler.TotalChunks}");
            GUILayout.Label($"CPU Threads:         {SystemInfo.processorCount}");
            GUILayout.Label($"Managed Threads:     {Process.GetCurrentProcess().Threads.Count}");

            GUILayout.Space(10);
        }

        if (showMeshStats)
        {
            GUILayout.Label("<b>Mesh Stats</b>", label);

            int pool = chunkManager != null ? chunkManager.meshPool?.Count ?? 0 : 0;

            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            int activeMeshes = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None)
                .Count(mf => mf.sharedMesh != null);

            int vertTotal = meshes.Sum(m => m.vertexCount);

            GUILayout.Label($"Active Meshes:  {activeMeshes}");
            GUILayout.Label($"Mesh Pool:      {pool}");
            GUILayout.Label($"All Meshes:     {meshes.Length}");
            GUILayout.Label($"Total Vertices: {vertTotal:N0}");
        }

        GUILayout.EndArea();
    }
    void DrawLoadBarWindow()
    {
        float avgNoise = Avg(Profiler.noiseTimes, Profiler.noiseLock);
        float avgBlock = Avg(Profiler.blockTimes, Profiler.blockLock);
        float avgDeco = Avg(Profiler.decoTimes, Profiler.decoLock);
        float avgMesh = Avg(Profiler.meshTimes, Profiler.meshLock);
        float avgUp = Avg(Profiler.uploadTimes, Profiler.uploadLock);

        GUILayout.BeginArea(new Rect(400, 10, 360, 160), "Load Bars", GUI.skin.window);
        GUILayout.Space(4);

        DrawLoadBar(avgNoise, avgBlock, avgDeco, avgMesh, avgUp);

        GUILayout.EndArea();
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

