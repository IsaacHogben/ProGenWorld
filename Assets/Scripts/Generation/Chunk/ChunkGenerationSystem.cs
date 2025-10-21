/*using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static GreedyMeshJob;

public class ChunkGenerationSystem
{
    private ChunkManager settings;
    private NoiseGenerator noiseGen;

    private readonly ConcurrentQueue<(int3 coord, float[] density)> completedNoise = new();
    public ConcurrentQueue<(int3 coord, float[] density)> CompletedNoiseQueue => completedNoise;

    private readonly Dictionary<int3, Chunk> chunks = new();
    private readonly HashSet<int3> generating = new();
    public int GeneratingCount => generating.Count;

    private readonly HashSet<int3> activeNoiseTasks = new();
    private SemaphoreSlim noiseLimiter;

    private readonly Stack<NativeArray<float>> densityPool = new();
    private readonly Queue<int3> frontier = new();
    private readonly HashSet<int3> deferredFrontier = new();
    private readonly Dictionary<int3, OpenFaces> openFaceMap = new();

    private int3 lastPlayerChunk;
    private bool firstFloodTriggered = false;
    private float lastScheduleTime;

    public int ActiveNoiseTasks => activeNoiseTasks.Count;

    int DensityCount => (settings.chunkSize + 1) * (settings.chunkSize + 1) * (settings.chunkSize + 1);

    public void Initialize(ChunkManager manager)
    {
        settings = manager;
        noiseGen = new NoiseGenerator(111);
        noiseLimiter = new SemaphoreSlim(Math.Max(1, settings.maxConcurrentNoiseTasks));

        for (int i = 0; i < settings.initialPoolSize; i++)
            densityPool.Push(new NativeArray<float>(DensityCount, Allocator.Persistent));
    }

    public void Start()
    {
        var playerChunk = WorldToChunkCoord(settings.player.transform.position);
        frontier.Enqueue(playerChunk);
    }

    public void Update()
    {
        if (Time.time - lastScheduleTime >= settings.scheduleInterval)
        {
            lastScheduleTime = Time.time;
            if (frontier.Count > 0) ScheduleVisibleChunksFloodFill();
        }

        int3 playerChunk = WorldToChunkCoord(settings.player.transform.position);
        if (!firstFloodTriggered || !playerChunk.Equals(lastPlayerChunk))
        {
            firstFloodTriggered = true;
            lastPlayerChunk = playerChunk;
            PromoteDeferredToFrontier(playerChunk);
            ScheduleVisibleChunksFloodFill();
        }
    }

    #region Scheduling
    void ScheduleVisibleChunksFloodFill()
    {
        if (settings.player == null || frontier.Count == 0) return;
        int spawnedThisTick = 0;
        int cap = Math.Max(1, settings.maxConcurrentNoiseTasks);

        while (frontier.Count > 0 && spawnedThisTick < cap)
        {
            var coord = frontier.Dequeue();
            if (chunks.ContainsKey(coord) || generating.Contains(coord)) continue;
            generating.Add(coord);
            spawnedThisTick++;
            ScheduleNoiseTask(coord);
        }
    }

    void ScheduleNoiseTask(int3 coord)
    {
        lock (activeNoiseTasks)
        {
            if (activeNoiseTasks.Contains(coord)) return;
            activeNoiseTasks.Add(coord);
        }

        Task.Run(async () =>
        {
            await noiseLimiter.WaitAsync();

            try
            {
                float[] density = noiseGen.FillDensity(coord, settings.chunkSize, DensityCount);
                completedNoise.Enqueue((coord, density));
            }
            catch (Exception ex)
            {
                Debug.LogError($"Noise task failed for {coord}: {ex}");
            }
            finally
            {
                noiseLimiter.Release();
                lock (activeNoiseTasks) activeNoiseTasks.Remove(coord);
            }
        });
    }
    #endregion

    #region Flood Fill
    void PromoteDeferredToFrontier(int3 center)
    {
        float maxDistSqr = settings.viewDistance * settings.viewDistance;
        List<int3> toPromote = new();

        foreach (var c in deferredFrontier)
        {
            if (chunks.ContainsKey(c) || generating.Contains(c)) continue;
            float d2 = math.lengthsq((float3)(c - center));
            if (d2 <= maxDistSqr) toPromote.Add(c);
        }

        foreach (var c in toPromote)
        {
            frontier.Enqueue(c);
            deferredFrontier.Remove(c);
        }
    }

    void ExpandFrontierFromFaces(int3 coord, OpenFaces faces)
    {
        if (faces == OpenFaces.None) return;

        var center = WorldToChunkCoord(settings.player.transform.position);
        float maxDistSqr = settings.viewDistance * settings.viewDistance;

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
            if (chunks.ContainsKey(n) || generating.Contains(n)) continue;

            float d2 = math.lengthsq((float3)(n - center));
            if (d2 <= maxDistSqr)
                frontier.Enqueue(n);
            else
                deferredFrontier.Add(n);
        }
    }
    #endregion

    #region Face Detection
    OpenFaces DetectOpenFaces(NativeArray<float> density)
    {
        OpenFaces flags = OpenFaces.None;
        int s = settings.chunkSize + 1;
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                {
                    float val = density[x + y * s + z * s * s];
                    if (val > settings.isoLevel)
                    {
                        if (x == 0) flags |= OpenFaces.NegX;
                        if (x == s - 1) flags |= OpenFaces.PosX;
                        if (y == 0) flags |= OpenFaces.NegY;
                        if (y == s - 1) flags |= OpenFaces.PosY;
                        if (z == 0) flags |= OpenFaces.NegZ;
                        if (z == s - 1) flags |= OpenFaces.PosZ;
                    }
                }
        return flags;
    }
    #endregion

    int3 WorldToChunkCoord(Vector3 pos)
    {
        return new int3(
            Mathf.FloorToInt(pos.x / settings.chunkSize),
            Mathf.FloorToInt(pos.y / settings.chunkSize),
            Mathf.FloorToInt(pos.z / settings.chunkSize)
        );
    }

    // Debug accessors
    public IReadOnlyDictionary<int3, OpenFaces> DebugOpenFaceMap => openFaceMap;
    public IReadOnlyCollection<int3> DebugDeferredFrontier => deferredFrontier;
    //public int DebugFrontierCount => frontier.Count;

    public void Dispose()
    {
        lock (densityPool)
        {
            while (densityPool.Count > 0)
            {
                var a = densityPool.Pop();
                if (a.IsCreated) a.Dispose();
            }
        }
    }
}
*/