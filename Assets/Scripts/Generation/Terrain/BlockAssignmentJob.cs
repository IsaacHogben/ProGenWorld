using Unity.Burst;
using Unity.Collections;
using Unity.Entities.UniversalDelegates;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Converts a chunk's density field into block IDs.
/// Runs after noise generation, before decorations.
/// </summary>
[BurstCompile]
public struct BlockAssignmentJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> density;     // Input: noise density field
    [WriteOnly] public NativeArray<byte> blockIds;    // Output: block ID field
    [ReadOnly] public int chunkSize;
    [ReadOnly] public int3 chunkCoord;             // x,y,z of chunk
    [ReadOnly] public BiomeData biomeData;

    public void Execute(int i)
    {
        float blockDensity = density[i];
        int3 index = IndexToXYZ(i);
        float aboveBlockDensity = GetAboveDensity(index);
        // Gradient: positive means terrain is getting less dense upward (approaching surface)
        // Negative means terrain is getting denser upward (unusual, but possible in overhangs)
        float verticalGradient = aboveBlockDensity - blockDensity;

        bool isSolid = blockDensity <= 0;
        bool isAirAbove = aboveBlockDensity > 0;
        bool isAboveWaterLevel = GetWorldYValue(index) >= -90;
        bool isApproachingSurface = verticalGradient - 0.002f > 0;
        BiomeHint biome = BiomeSampler.SampleBiome(biomeData.grid, biomeData.resolution, chunkSize, index.x, index.z);

        byte block;
        if (!isSolid)
        {
            if (isAboveWaterLevel)
                block = (byte)BlockType.Air;
            else
                block = (byte)BlockType.Water;
        }
        else
        {
            if (biome.primary == 1)
                block = (byte)BlockType.Stone;
            else
                block = (byte)BlockType.Log;
        }
        /*else if (isAirAbove && isAboveWaterLevel)
        {
            // Surface layer - transition based on gradient steepness
            if (verticalGradient > 0.005f)          // More is less
                block = (byte)BlockType.Grass;      // Gentle slopes
            else if (verticalGradient > 0.002f)
                block = (byte)BlockType.Dirt;       // Moderate slopes
            else
                block = (byte)BlockType.Stone;      // Steep cliffs
        }
        else if (isApproachingSurface)
        {
            block = (byte)BlockType.Dirt;
        }
        else
        {
            block = (byte)BlockType.Stone;
        }*/

            blockIds[i] = block;
    }

    int GetWorldYValue(int3 index)
    {
        return index.y + chunkCoord.y * chunkSize;
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
