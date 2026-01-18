using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class BlockGenSystem
{
    public struct Config
    {
        public int chunkSize;
        public Func<LODLevel, int> GetSampleRes;
        public BiomeDataManager biomeDataManager;
    }

    private Config cfg;
    private Action<LODLevel, NativeArray<float>> returnDensity;

    private struct BlockJobInfo
    {
        public LODLevel lod;
        public NativeArray<float> density;
        public NativeArray<byte> blockIds;
        public JobHandle handle;
    }

    private readonly Dictionary<int3, BlockJobInfo> jobs = new();
    public int ActiveJobs => jobs.Count;

    public event Action<int3, LODLevel, NativeArray<byte>> OnBlockGenCompleted;

    private static readonly List<int3> tmpCoords = new();

    public void Initialize(Config config, Action<LODLevel, NativeArray<float>> returnDensity)
    {
        cfg = config;
        this.returnDensity = returnDensity;
    }

    public void GenerateBlocks(int3 coord, LODLevel lod, NativeArray<float> density, BiomeData biomeData)
    {
        if (cfg.biomeDataManager == null || !cfg.biomeDataManager.IsInitialized)
        {
            Debug.LogError("BiomeDataManager not initialized!");
            return;
        }

        // If somehow we already have a job for this coord, complete and clean it first
        if (jobs.TryGetValue(coord, out var existing))
        {
            existing.handle.Complete();
            if (existing.density.IsCreated)
                returnDensity(existing.lod, existing.density);
            if (existing.blockIds.IsCreated)
                existing.blockIds.Dispose();
            jobs.Remove(coord);
        }

        int sampleRes = cfg.GetSampleRes(lod);
        int chunkLodSize = cfg.chunkSize / sampleRes;
        int voxelCount = (chunkLodSize + 1) * (chunkLodSize + 1) * (chunkLodSize + 1);

        var blockIds = new NativeArray<byte>(voxelCount, Allocator.Persistent);
        ChunkMemDebug.ActiveBlockIdArrays++;
        ChunkMemDebug.TotalBlockIdAlloc++;

        var job = new BlockAssignmentJob
        {
            density = density,
            blockIds = blockIds,
            chunkSize = chunkLodSize,
            chunkCoord = coord,
            waterLevel = cfg.biomeDataManager.GetWaterLevel(),

            // Pass biome data
            biomeHints = biomeData.grid,
            biomeResolution = biomeData.resolution,
            biomes = cfg.biomeDataManager.GetBiomeDefinitions()
        };

        Profiler.StartBlock();

        JobHandle handle = job.Schedule(voxelCount, 64);

        jobs[coord] = new BlockJobInfo
        {
            lod = lod,
            density = density,
            blockIds = blockIds,
            handle = handle
        };
    }

    public void Update()
    {
        if (jobs.Count == 0)
            return;

        tmpCoords.Clear();
        tmpCoords.AddRange(jobs.Keys);

        foreach (var coord in tmpCoords)
        {
            var info = jobs[coord];
            if (!info.handle.IsCompleted)
                continue;

            info.handle.Complete();
            Profiler.EndBlock();

            if (info.density.IsCreated)
                returnDensity(info.lod, info.density);

            OnBlockGenCompleted?.Invoke(coord, info.lod, info.blockIds);

            jobs.Remove(coord);
        }
    }

    public void CullOutOfRange(
        Vector3 centerWorld,
        float maxDistanceChunks,
        Func<int3, float> getDistanceChunks,
        Action<int3> onCancelled)
    {
        if (jobs.Count == 0)
            return;

        tmpCoords.Clear();
        tmpCoords.AddRange(jobs.Keys);

        foreach (var coord in tmpCoords)
        {
            float dist = getDistanceChunks(coord);
            if (dist <= maxDistanceChunks)
                continue;

            var info = jobs[coord];

            info.handle.Complete();

            if (info.density.IsCreated)
                returnDensity(info.lod, info.density);

            if (info.blockIds.IsCreated)
            {
                ChunkMemDebug.ActiveBlockIdArrays--;
                info.blockIds.Dispose();
            }

            jobs.Remove(coord);
            onCancelled?.Invoke(coord);
        }
    }

    public void Shutdown()
    {
        tmpCoords.Clear();
        tmpCoords.AddRange(jobs.Keys);

        foreach (var coord in tmpCoords)
        {
            var info = jobs[coord];
            info.handle.Complete();

            if (info.density.IsCreated)
                returnDensity(info.lod, info.density);

            if (info.blockIds.IsCreated)
            {
                ChunkMemDebug.ActiveBlockIdArrays--;
                info.blockIds.Dispose();
            }
        }

        jobs.Clear();
    }
}