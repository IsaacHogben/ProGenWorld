using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public struct DecorationJob : IJob
{
    [ReadOnly] public int3 chunkCoord;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public int indexSize;
    [ReadOnly] public LODLevel lod;
    [ReadOnly] public int waterLevel;
    public Unity.Mathematics.Random rng;

    // Block data
    public NativeArray<byte> blockIds;
    public NativeList<PendingBlockWrite> pendingWrites;

    // Biome data
    [ReadOnly] public NativeArray<BiomeHint> biomeHints;
    [ReadOnly] public int biomeResolution;
    [ReadOnly] public NativeArray<BiomeDefinition> biomes;
    [ReadOnly] public NativeArray<DecorationData> decorations;
    [ReadOnly] public NativeArray<byte> spawnBlocks;

    public void Execute()
    {
        for (int z = 0; z < chunkSize; z++)
        {
            for (int y = 0; y < chunkSize; y++)
            {
                for (int x = 0; x < chunkSize; x++)
                {
                    byte currentBlock = blockIds[GetIndex(z, y, x)];
                    byte aboveBlock = blockIds[GetIndex(z, y + 1, x)];

                    bool surfaceLayer = aboveBlock == (byte)BlockType.Air;
                    bool isAboveWaterLevel = GetWorldYValue(y) > waterLevel;

                    if (!surfaceLayer || !isAboveWaterLevel)
                        continue;

                    // Sample biome at this location
                    BiomeHint hint = SampleBiome(x, z);

                    // Try primary biome decorations
                    if (hint.primaryBiome < biomes.Length)
                    {
                        BiomeDefinition biome = biomes[hint.primaryBiome];
                        TryPlaceDecorations(biome, currentBlock, aboveBlock, x, y, z);
                    }

                    // Optionally try secondary biome with reduced chance based on blend
                    // (Uncomment if you want decorations from both biomes)
                    /*
                    if (hint.secondaryBiome < biomes.Length && hint.biomeBlend > 128)
                    {
                        BiomeData secondaryBiome = biomes[hint.secondaryBiome];
                        float blendFactor = (hint.biomeBlend - 128) / 127f;
                        TryPlaceDecorations(secondaryBiome, currentBlock, x, y, z, blendFactor);
                    }
                    */
                }
            }
        }
    }

    private void TryPlaceDecorations(BiomeDefinition biome, byte currentBlock, byte aboveBlock, int x, int y, int z)
    {
        int decorationEnd = biome.decorationStartIndex + biome.decorationCount;

        for (int i = biome.decorationStartIndex; i < decorationEnd; i++)
        {
            if (i >= decorations.Length)
                break;

            if (aboveBlock != (byte)BlockType.Air)
                break;

            DecorationData decoration = decorations[i];

            // Check if can spawn on this block type
            if (!CanSpawnOn(decoration, currentBlock))
                continue;

            // Check spawn chance
            float adjustedChance = decoration.spawnChance;
            if (rng.NextFloat() > adjustedChance)
                continue;

            // Place the decoration
            PlaceDecoration(decoration, x, y, z);
        }
    }

    private bool CanSpawnOn(DecorationData decoration, byte blockType)
    {
        int start = decoration.spawnBlockStartIndex;
        int end = start + decoration.spawnBlockCount;

        for (int i = start; i < end; i++)
        {
            if (i >= spawnBlocks.Length)
                return false;

            if (spawnBlocks[i] == blockType)
                return true;
        }

        return false;
    }

    private void PlaceDecoration(DecorationData decoration, int x, int y, int z)
    {
        switch ((DecorationCategory)decoration.category)
        {
            case DecorationCategory.Tree:
                PlaceTree((DecorationType.Tree)decoration.typeID, x, y, z);
                break;

            case DecorationCategory.Vegetation:
                PlaceVegetation((DecorationType.Vegetation)decoration.typeID, x, y, z);
                break;

            case DecorationCategory.Rock:
                PlaceRock((DecorationType.Rock)decoration.typeID, x, y, z);
                break;

            case DecorationCategory.Alien:
                PlaceAlien((DecorationType.Alien)decoration.typeID, x, y, z);
                break;
        }
    }

    // ========================================================================
    // TREE PLACEMENT
    // ========================================================================

    private void PlaceTree(DecorationType.Tree treeType, int x, int y, int z)
    {
        TreeGenerator.Generate(
            treeType,
            x, y, z,
            ref rng,
            ApplyBlock);
    }

    // ========================================================================
    // VEGETATION PLACEMENT
    // ========================================================================

    private void PlaceVegetation(DecorationType.Vegetation vegType, int x, int y, int z)
    {
        switch (vegType)
        {
            case DecorationType.Vegetation.Grass:
                // MakeGrass(x, y, z);
                break;

            case DecorationType.Vegetation.TallGrass:
                // MakeTallGrass(x, y, z);
                break;

            case DecorationType.Vegetation.Fern:
                // MakeFern(x, y, z);
                break;

            case DecorationType.Vegetation.Mushroom:
                // MakeMushroom(x, y, z);
                break;

            case DecorationType.Vegetation.Flower:
                // MakeFlower(x, y, z);
                break;

            case DecorationType.Vegetation.Bush:
                // MakeBush(x, y, z);
                break;
        }
    }

    // ========================================================================
    // ROCK PLACEMENT
    // ========================================================================

    private void PlaceRock(DecorationType.Rock rockType, int x, int y, int z)
    {
        switch (rockType)
        {
            case DecorationType.Rock.SmallBoulder:
                // MakeSmallBoulder(x, y, z);
                break;

            case DecorationType.Rock.LargeBoulder:
                // MakeLargeBoulder(x, y, z);
                break;

            case DecorationType.Rock.CrystalCluster:
                // MakeCrystalCluster(x, y, z);
                break;

            case DecorationType.Rock.Stalagmite:
                // MakeStalagmite(x, y, z);
                break;
        }
    }

    // ========================================================================
    // ALIEN PLACEMENT
    // ========================================================================

    private void PlaceAlien(DecorationType.Alien alienType, int x, int y, int z)
    {
        switch (alienType)
        {
            case DecorationType.Alien.FungalTree:
                // MakeFungalTree(x, y, z);
                break;

            case DecorationType.Alien.GlowingSpore:
                // MakeGlowingSpore(x, y, z);
                break;

            case DecorationType.Alien.TentaclePlant:
                // MakeTentaclePlant(x, y, z);
                break;

            case DecorationType.Alien.BioluminescentVine:
                // MakeBioluminescentVine(x, y, z);
                break;

            case DecorationType.Alien.CrystalFormation:
                // MakeCrystalFormation(x, y, z);
                break;
        }
    }

    // ========================================================================
    // BIOME SAMPLING
    // ========================================================================

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

    // ========================================================================
    // BLOCK PLACEMENT
    // ========================================================================

    void ApplyBlock(int x, int y, int z, BlockType block)
    {
        byte blockId = (byte)block;
        int index = x + y * indexSize + z * indexSize * indexSize;
        int3 localPos = new int3(x, y, z);
        int3 worldPos = chunkCoord * chunkSize + localPos;

        int3 targetChunk = new int3(
            (int)math.floor(worldPos.x / (float)chunkSize),
            (int)math.floor(worldPos.y / (float)chunkSize),
            (int)math.floor(worldPos.z / (float)chunkSize)
        );

        int3 targetLocal = new int3(
            worldPos.x - targetChunk.x * chunkSize,
            worldPos.y - targetChunk.y * chunkSize,
            worldPos.z - targetChunk.z * chunkSize
        );

        if (targetLocal.Equals(chunkCoord) && x != 0 && y != 0 && z != 0)
        {
            blockIds[index] = blockId;
        }
        else
        {
            pendingWrites.Add(new PendingBlockWrite
            {
                targetChunk = targetChunk,
                localPos = targetLocal,
                blockId = blockId,
                mode = PendingWriteMode.ReplaceAir,
                isMirror = false,
            });
        }
    }

    // ========================================================================
    // UTILITY FUNCTIONS
    // ========================================================================

    private readonly int GetIndex(int z, int y, int x)
    {
        return x + y * indexSize + z * indexSize * indexSize;
    }

    private int GetWorldYValue(int y)
    {
        return y + chunkCoord.y * chunkSize;
    }
}