using Unity.Burst;
using Unity.Collections;
using Unity.Entities.UniversalDelegates;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Converts a chunk's density field into block IDs.
/// Runs after noise generation, before meshing.
/// </summary>
[BurstCompile]
public struct BlockAssignmentJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> density;     // Input: noise density field
    [WriteOnly] public NativeArray<byte> blockIds;    // Output: block ID field
    [ReadOnly] public int chunkSize;               // 
    [ReadOnly] public int voxelCount;               // (chunkSize + 1)^3 typically
    [ReadOnly] public int3 startingCoord;             // x,y,z of chunk

    public void Execute(int i)
    {
        float blockDensity = density[i];
        int3 index = IndexToXYZ(i);
        float aboveBlockDensity = GetAboveDensity(index);
        byte block;

        if (blockDensity > 0)
        {
            block = (byte)BlockType.Air;
        }

        else if (aboveBlockDensity > 0)
        {
            block = (byte)BlockType.Grass;
        }
        else if (blockDensity < aboveBlockDensity)
            block = (byte)BlockType.Dirt;
        else
            block = (byte)BlockType.Stone;

        blockIds[i] = block;
    }

    int CoordToIndex(int x, int y, int z)
    {
        return voxelCount >= (x + y * (chunkSize + 1) + z * (chunkSize + 1) * (chunkSize + 1)) ? voxelCount : (x + y * (chunkSize + 1) + z * (chunkSize + 1) * (chunkSize + 1));
    }

    float GetAboveDensity(int3 v)
    {
        int r;
        if (TryGetIndex(v.x, v.y + 1, v.z, out r))
            return density[r];

        return -1;
    }
    int3 IndexToXYZ(int i)
    {
        return new int3(
            i % (chunkSize + 1),
            (i / (chunkSize + 1)) % (chunkSize + 1),
            i / ((chunkSize + 1) * (chunkSize + 1))
        );
    }

    bool TryGetIndex(int x, int y, int z, out int idx)
    {
        if (x < 0 || y < 0 || z < 0 || x >= (chunkSize + 1) || y >= (chunkSize + 1) || z >= (chunkSize + 1))
        {
            idx = -1;
            return false;
        }

        idx = x + y * (chunkSize + 1) + z * (chunkSize + 1) * (chunkSize + 1);
        return true;
    }
}
