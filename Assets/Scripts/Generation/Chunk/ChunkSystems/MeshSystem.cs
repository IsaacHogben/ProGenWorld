using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class MeshSystem
{
    private struct MeshJobInfo
    {
        public JobHandle handle;
        public MeshData data;
        public NativeArray<byte> blockIds;
        public bool keepBlocks;
    }

    public struct Config
    {
        public int chunkSize;
        public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
        public Func<LODLevel, int> GetMeshRes;
        public Func<LODLevel, int> GetSampleRes;
        public Func<LODLevel, int> GetBlockSize;
    }

    private Config config;

    // Provided by ChunkManager
    private Func<Mesh> rentMesh;
    private Action<Mesh> returnMesh;

    private Func<MeshData> allocMeshData;
    private Action<MeshData> freeMeshData;

    private Func<int3, Chunk> getChunk;
    private Func<int3, Vector3> chunkToWorldPos;

    private Action<int3> markMeshed; // updates lastMeshFrame
    private Func<int3, bool> canMeshNow; // debounce checker

    private readonly Dictionary<int3, MeshJobInfo> meshJobs = new();
    private Dictionary<int3, MeshData> meshJobData = new();
    public int ActiveJobs => meshJobs.Count;

    private Queue<(int3 coord, MeshData data)> completed = new();

    public event Action<int3, MeshData> OnMeshReady;
    public void Initialize(
        Config cfg,
        Func<Mesh> rentMesh,
        Action<Mesh> returnMesh,
        Func<MeshData> allocMeshData,
        Action<MeshData> freeMeshData,
        Func<int3, Chunk> getChunk,
        Func<int3, Vector3> chunkToWorldPos,
        Action<int3> markMeshed,
        Func<int3, bool> canMeshNow)
    {
        config = cfg;

        this.rentMesh = rentMesh;
        this.returnMesh = returnMesh;
        this.allocMeshData = allocMeshData;
        this.freeMeshData = freeMeshData;

        this.getChunk = getChunk;
        this.chunkToWorldPos = chunkToWorldPos;

        this.markMeshed = markMeshed;
        this.canMeshNow = canMeshNow;
    }

    public void RequestMesh(int3 coord, NativeArray<byte> blocks, LODLevel lod, bool keepBlocks)
    {
        if (!canMeshNow(coord))
            return;

        int meshRes = config.GetMeshRes(lod);
        int sampleRes = config.GetSampleRes(lod);
        int blockSize = config.GetBlockSize(lod);

        var meshData = allocMeshData();

        meshData.coord = coord;
        meshData.meshRes = meshRes;
        meshData.lod = lod;

        var job = new GreedyMeshJob
        {
            blockArray = blocks,
            blockDb = config.blockDb,
            chunkSize = config.chunkSize / sampleRes,
            blockSize = blockSize,
            meshData = meshData
        };

        Profiler.StartMesh();
        JobHandle handle = job.Schedule();

        meshJobs[coord] = new MeshJobInfo
        {
            handle = handle,
            data = meshData,
            blockIds = blocks,
            keepBlocks = keepBlocks
        };
    }
    public void Update()
    {
        if (meshJobs.Count == 0)
            return;

        tempList.Clear();
        tempList.AddRange(meshJobs.Keys);

        foreach (var coord in tempList)
        {
            var info = meshJobs[coord];

            if (!info.handle.IsCompleted)
                continue;

            info.handle.Complete();
            Profiler.EndMesh();

            // Prepare data for callback
            var meshData = info.data;

            // If we do not want to keep blockIds (Far LOD),
            // free them here.
            if (!info.keepBlocks && info.blockIds.IsCreated)
            {
                ChunkMemDebug.ActiveBlockIdArrays--;
                info.blockIds.Dispose();
            }

            meshJobs.Remove(coord);

            completed.Enqueue((coord, meshData));
        }

        while (completed.Count > 0)
        {
            var (coord, data) = completed.Dequeue();
            markMeshed(coord);
            OnMeshReady?.Invoke(coord, data);
        }
    }

    public bool IsMeshInProgress(int3 coord)
    {
        return meshJobs.ContainsKey(coord);
    }
    public void ReleaseMeshData(MeshData data)
    {
        freeMeshData?.Invoke(data);
    }
    private static List<int3> tempList = new();
}
