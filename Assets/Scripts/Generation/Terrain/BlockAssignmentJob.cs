using Unity.Burst;
using Unity.Collections;
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
    [ReadOnly] public NativeArray<float> density;
    [WriteOnly] public NativeArray<byte> blockIds;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public int3 chunkCoord;
    [ReadOnly] public int waterLevel;

    // Biome data
    [ReadOnly] public NativeArray<BiomeHint> biomeHints;
    [ReadOnly] public int biomeResolution;
    [ReadOnly] public NativeArray<BiomeDefinition> biomes;

    public void Execute(int i)
    {
        float blockDensity = density[i];
        int3 index = IndexToXYZ(i);
        float aboveBlockDensity = GetAboveDensity(index);

        // Gradient: positive = terrain getting less dense upward (approaching surface)
        float verticalGradient = aboveBlockDensity - blockDensity;

        bool isSolid = blockDensity <= 0;
        bool isAirAbove = aboveBlockDensity > 0;
        int worldY = GetWorldYValue(index);
        bool isAboveWaterLevel = worldY > waterLevel;

        // Sample biome at this location
        BiomeHint hint = SampleBiome(index.x, index.z);

        byte block;

        if (!isSolid)
        {
            // Not solid - air or water
            if (isAboveWaterLevel)
                block = (byte)BlockType.Air;
            else
                block = (byte)BlockType.Water;
        }
        else if (isAirAbove && isAboveWaterLevel)
        {
            // Surface block - use biome's gradient-based palette
            block = GetSurfaceBlock(hint, verticalGradient);
        }
        else if (isAirAbove && !isAboveWaterLevel)
        {
            // Underwater surface - use sediment block
            block = GetSedimentBlock(hint);
        }
        else if (verticalGradient > 0)
        {
            // Subsurface - approaching surface from below
            block = GetSubsurfaceBlock(hint, verticalGradient);
        }
        else
        {
            // Deep/core block
            block = GetDeepBlock(hint);
        }

        blockIds[i] = block;
    }

    private byte GetSurfaceBlock(BiomeHint hint, float gradient)
    {
        // Blend between primary and secondary biome based on hint.biomeBlend
        BiomeDefinition primaryBiome = biomes[hint.primaryBiome];

        // Use gradient thresholds from primary biome
        if (gradient > primaryBiome.gradientThreshold1)
            return primaryBiome.surfaceBlock;        // Gentle slopes
        else if (gradient > primaryBiome.gradientThreshold2)
            return primaryBiome.subsurfaceBlock;     // Moderate slopes
        else
            return primaryBiome.deepBlock;           // Steep cliffs
    }

    private byte GetSubsurfaceBlock(BiomeHint hint, float gradient)
    {
        BiomeDefinition primaryBiome = biomes[hint.primaryBiome];

        // Check if we're close to surface or deeper
        if (gradient > primaryBiome.gradientThreshold2)
            return primaryBiome.subsurfaceBlock;
        else
            return primaryBiome.deepBlock;
    }

    private byte GetDeepBlock(BiomeHint hint)
    {
        BiomeDefinition primaryBiome = biomes[hint.primaryBiome];
        return primaryBiome.deepBlock;
    }

    private byte GetSedimentBlock(BiomeHint hint)
    {
        BiomeDefinition primaryBiome = biomes[hint.primaryBiome];
        return primaryBiome.sedimentBlock;
    }

    private BiomeHint SampleBiome(int localX, int localZ)
    {
        int side = biomeResolution + 1;
        float u = localX / (float)chunkSize;
        float v = localZ / (float)chunkSize;
        float fx = u * biomeResolution;
        float fz = v * biomeResolution;
        int x = math.clamp((int)math.floor(fx), 0, biomeResolution);
        int z = math.clamp((int)math.floor(fz), 0, biomeResolution);
        return biomeHints[x + z * side];
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
        return 1f; // Assume air above if out of bounds
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