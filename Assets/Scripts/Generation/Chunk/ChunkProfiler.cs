using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics;
using System.Threading;

public class ChunkProfiler : MonoBehaviour
{
    [SerializeField] private ChunkManager chunkManager;

    [Header("Sampling")]
    public float logInterval = 5f; // seconds between logs
    public bool showOnScreen = true;
    public bool detailed = true;

    static readonly Queue<float> noiseTimes = new();
    static readonly Queue<float> meshTimes = new();
    static readonly Queue<float> uploadTimes = new();

    static float lastLogTime;
    static float noiseStart, meshStart, uploadStart;

    // Dynamic stats from ChunkManager
    public static int ActiveNoiseTasks;
    public static int ActiveMeshJobs;
    public static int QueuedUploads;
    public static int TotalChunks;

    readonly GUIStyle labelStyle = new GUIStyle();

    void Awake()
    {
        labelStyle.fontSize = 14;
        labelStyle.normal.textColor = Color.white;
    }

    void Update()
    {
        // Merge thread timings safely
        lock (ChunkProfilerTimes.noiseLock)
        {
            while (ChunkProfilerTimes.noiseDurations.Count > 0)
                noiseTimes.Enqueue(ChunkProfilerTimes.noiseDurations.Dequeue());
        }
        if (Time.time - lastLogTime >= logInterval)
        {
            lastLogTime = Time.time;
            LogAverages();
        }
    }

    void OnGUI()
    {
        if (!showOnScreen) return;

        // collect averages
        var avgNoise = GetAverage(noiseTimes);
        var avgMesh = GetAverage(meshTimes);
        var avgUpload = GetAverage(uploadTimes);
        var total = avgNoise + avgMesh + avgUpload;
        float fps = 1f / Time.deltaTime;
        float frameTime = Time.deltaTime * 1000f;

        GUILayout.BeginArea(new Rect(10, 10, 300, 520), "Chunk Profiler", GUI.skin.window);

        GUILayout.Label($"<b>Performance</b>", labelStyle);
        GUILayout.Label($"FPS: {fps:F1}");
        GUILayout.Label($"Frame Time: {frameTime:F1} ms");
        GUILayout.Space(4);
        GUILayout.Label($"Noise:  {avgNoise:F2} ms");
        GUILayout.Label($"Mesh:   {avgMesh:F2} ms");
        GUILayout.Label($"Upload: {avgUpload:F2} ms");
        GUILayout.Label($"Total per chunk: {total:F2} ms");
        GUILayout.Space(8);

        if (detailed)
        {
            GUILayout.Label("<b>Generation Stats</b>", labelStyle);
            GUILayout.Label($"Active Noise Tasks: {ActiveNoiseTasks}");
            GUILayout.Label($"Active Mesh Jobs:   {ActiveMeshJobs}");
            GUILayout.Label($"Queued Uploads:     {QueuedUploads}");
            GUILayout.Label($"Total Chunks:       {TotalChunks}");
            GUILayout.Label($"CPU Threads:        {SystemInfo.processorCount}");
            GUILayout.Label($"Managed Threads:    {Process.GetCurrentProcess().Threads.Count}");
            GUILayout.Space(8);
        }

        GUILayout.Label("<b>Adaptive Performance</b>", labelStyle);
        if (chunkManager != null)
        {
            GUILayout.Label($"Avg Frame Time: {chunkManager.AvgFrameTime:F1} ms");
            GUILayout.Label($"Noise Tasks: {chunkManager.CurrentNoiseTasks}");
            GUILayout.Label($"Mesh Uploads/Frame: {chunkManager.CurrentMeshUploads}");
            GUILayout.Label($"Schedule Interval: {chunkManager.CurrentScheduleInterval:F2}s");
            GUILayout.Label($"Pool Size: {chunkManager.CurrentPoolSize}");
        }
        else
        {
            GUILayout.Label("<ChunkManager not linked>");
        }

        GUILayout.EndArea();
    }


    public static void NoiseStart() => noiseStart = Time.realtimeSinceStartup * 1000f;
    public static void NoiseEnd() => noiseTimes.Enqueue(Time.realtimeSinceStartup * 1000f - noiseStart);

    public static void MeshStart() => meshStart = Time.realtimeSinceStartup * 1000f;
    public static void MeshEnd() => meshTimes.Enqueue(Time.realtimeSinceStartup * 1000f - meshStart);

    public static void UploadStart() => uploadStart = Time.realtimeSinceStartup * 1000f;
    public static void UploadEnd() => uploadTimes.Enqueue(Time.realtimeSinceStartup * 1000f - uploadStart);

    public static void ReportNoiseCount(int count) => ActiveNoiseTasks = count;
    public static void ReportMeshCount(int count) => ActiveMeshJobs = count;
    public static void ReportUploadQueue(int count) => QueuedUploads = count;
    public static void ReportChunkCount(int count) => TotalChunks = count;

    static void LogAverages()
    {
        UnityEngine.Debug.Log(
            $"[ChunkProfiler] Avg Noise: {GetAverage(noiseTimes):F2} ms | " +
            $"Mesh: {GetAverage(meshTimes):F2} ms | Upload: {GetAverage(uploadTimes):F2} ms | " +
            $"Chunks: {TotalChunks} | NoiseTasks: {ActiveNoiseTasks} | MeshJobs: {ActiveMeshJobs}");
    }

    static float GetAverage(Queue<float> q)
    {
        if (q.Count == 0) return 0f;
        float sum = 0;
        foreach (var v in q) sum += v;
        while (q.Count > 100) q.Dequeue(); // cap queue size
        return sum / q.Count;
    }
}

public static class ChunkProfilerTimes
{
    public static readonly Queue<float> noiseDurations = new();
    public static readonly object noiseLock = new();
}