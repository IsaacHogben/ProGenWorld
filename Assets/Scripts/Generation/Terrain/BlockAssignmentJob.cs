using Unity.Burst;
using Unity.Collections;
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
    [ReadOnly] public int chunkSize;                  // (chunkSize + 1)^3 typically

    public void Execute(int i)
    {
        float d = density[i];

        blockIds[i] = (byte)(d > isoLevel ? BlockType.Air : BlockType.Dirt);
        //string s = (d > isoLevel ? BlockType.Air : BlockType.Stone).ToString();
        //Debug.Log(s);
    }

    bool GetBlock(int3 coord)
    {
        int i = coord.x + coord.y * (chunkSize + 1) + coord.z * (chunkSize + 1) * (chunkSize + 1);
        return density[i] > isoLevel ? true : false;
    }
}
