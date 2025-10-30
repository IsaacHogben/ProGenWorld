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
    [ReadOnly] public float isoLevel;                 // Threshold for solid/air
    [ReadOnly] public int chunkSize;               // 
    [ReadOnly] public int voxelCount;               // (chunkSize + 1)^3 typically
    [ReadOnly] public int3 startingCoord;             // x,y,z of chunk

    public void Execute(int i)
    {
        float blockDensity = density[i];
        float aboveBlockDensity = GetAboveDensity(i);

        if (blockDensity > isoLevel)
        {
            blockIds[i] = (byte)BlockType.Air;
        }

        else if (aboveBlockDensity > isoLevel)
        {
            blockIds[i] = (byte)BlockType.Grass;
        }
        else if (blockDensity < aboveBlockDensity)
            blockIds[i] = (byte)BlockType.Dirt;
        else
            blockIds[i] = (byte)BlockType.Stone;

    }

    int CoordToIndex(int x, int y, int z)
    {
        return voxelCount >= (x + y * (chunkSize + 1) + z * (chunkSize + 1) * (chunkSize + 1)) ? voxelCount : (x + y * (chunkSize + 1) + z * (chunkSize + 1) * (chunkSize + 1));
    }

    float GetAboveDensity(int i)
    {
        int3 v = IndexToXYZ(i);
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
