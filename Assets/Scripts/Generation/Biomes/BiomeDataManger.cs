using System.Collections.Generic;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;

/// <summary>
/// Converts ScriptableObject definitions to Burst-compatible NativeArrays
/// </summary>
public class BiomeDataManager : MonoBehaviour
{
    [Header("Definitions")]
    [Tooltip("All terrain type definitions")]
    public TerrainTypeDefinitionSO[] terrainTypes;

    [Tooltip("All biome definitions")]
    public BiomeDefinitionSO[] biomes;

    [Header("Settings")]
    [Tooltip("Water level Y coordinate")]
    public int waterLevel = -90;

    // Native data for jobs
    private NativeArray<BiomeDefinition> biomeDefinitions;
    private NativeArray<TerrainTypeData> terrainTypeData;
    private NativeArray<float> allCurveData;
    private NativeArray<BiomeData> biomeData;
    private NativeArray<DecorationData> decorationData;
    private NativeArray<byte> decorationSpawnBlocks;
    private NativeArray<byte> terrainAllowedBiomes;

    private bool isInitialized = false;

    public void Initialize()
    {
        if (isInitialized)
        {
            Debug.LogWarning("BiomeDataManager already initialized!");
            return;
        }

        if (terrainTypes == null || terrainTypes.Length == 0)
        {
            Debug.LogError("No terrain types assigned!");
            return;
        }

        if (biomes == null || biomes.Length == 0)
        {
            Debug.LogError("No biomes assigned!");
            return;
        }

        ConvertTerrainTypes();
        ConvertBiomes();

        isInitialized = true;
        Debug.Log($"BiomeDataManager initialized: {terrainTypes.Length} terrain types, {biomes.Length} biomes");
    }

    private void ConvertTerrainTypes()
    {
        // Count total allowed biomes across all terrains
        int totalAllowedBiomes = 0;
        foreach (var terrainType in terrainTypes)
        {
            totalAllowedBiomes += terrainType.allowedBiomes.Length;
        }

        // Calculate total curve samples
        int totalCurveSamples = 0;
        foreach (var terrainType in terrainTypes)
        {
            totalCurveSamples += terrainType.curveResolution;
        }

        // Allocate
        terrainTypeData = new NativeArray<TerrainTypeData>(terrainTypes.Length, Allocator.Persistent);
        allCurveData = new NativeArray<float>(totalCurveSamples, Allocator.Persistent);
        terrainAllowedBiomes = new NativeArray<byte>(totalAllowedBiomes, Allocator.Persistent);

        int curveDataIndex = 0;
        int allowedBiomesIndex = 0;

        for (int i = 0; i < terrainTypes.Length; i++)
        {
            var terrainType = terrainTypes[i];

            // Bake curve
            float[] bakedCurve = terrainType.BakeCurve();
            for (int j = 0; j < bakedCurve.Length; j++)
            {
                allCurveData[curveDataIndex + j] = bakedCurve[j];
            }

            // Copy allowed biomes
            int allowedBiomesStart = allowedBiomesIndex;
            for (int j = 0; j < terrainType.allowedBiomes.Length; j++)
            {
                terrainAllowedBiomes[allowedBiomesIndex++] = terrainType.allowedBiomes[j];
            }

            // Convert to struct
            terrainTypeData[i] = terrainType.ToTerrainTypeData(curveDataIndex, allowedBiomesStart);

            curveDataIndex += terrainType.curveResolution;
        }
    }

    private void ConvertBiomes()
    {
        // Count total decorations and spawn blocks
        int totalDecorations = 0;
        int totalSpawnBlocks = 0;

        foreach (var biome in biomes)
        {
            totalDecorations += biome.trees.Length;
            totalDecorations += biome.vegetation.Length;
            totalDecorations += biome.rocks.Length;
            totalDecorations += biome.aliens.Length;

            foreach (var tree in biome.trees)
                totalSpawnBlocks += tree.spawnOnBlocks.Length;
            foreach (var veg in biome.vegetation)
                totalSpawnBlocks += veg.spawnOnBlocks.Length;
            foreach (var rock in biome.rocks)
                totalSpawnBlocks += rock.spawnOnBlocks.Length;
            foreach (var alien in biome.aliens)
                totalSpawnBlocks += alien.spawnOnBlocks.Length;
        }

        // Allocate
        biomeData = new NativeArray<BiomeData>(biomes.Length, Allocator.Persistent);
        biomeDefinitions = new NativeArray<BiomeDefinition>(biomes.Length, Allocator.Persistent);
        decorationData = new NativeArray<DecorationData>(totalDecorations, Allocator.Persistent);
        decorationSpawnBlocks = new NativeArray<byte>(totalSpawnBlocks, Allocator.Persistent);

        int decorationIndex = 0;
        int spawnBlockIndex = 0;

        for (int i = 0; i < biomes.Length; i++)
        {
            var biome = biomes[i];
            int decorationStartIndex = decorationIndex;

            // Trees
            foreach (var tree in biome.trees)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in tree.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Tree,
                    typeID = (byte)tree.treeType,
                    spawnChance = tree.spawnChance / 10000f, // Convert to 0-1
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = tree.spawnOnBlocks.Length
                };
            }

            // Vegetation
            foreach (var veg in biome.vegetation)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in veg.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Vegetation,
                    typeID = (byte)veg.vegetationType,
                    spawnChance = veg.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = veg.spawnOnBlocks.Length
                };
            }

            // Rocks
            foreach (var rock in biome.rocks)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in rock.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Rock,
                    typeID = (byte)rock.rockType,
                    spawnChance = rock.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = rock.spawnOnBlocks.Length
                };
            }

            // Aliens
            foreach (var alien in biome.aliens)
            {
                int spawnBlockStart = spawnBlockIndex;
                foreach (var block in alien.spawnOnBlocks)
                    decorationSpawnBlocks[spawnBlockIndex++] = (byte)block;

                decorationData[decorationIndex++] = new DecorationData
                {
                    category = (byte)DecorationCategory.Alien,
                    typeID = (byte)alien.alienType,
                    spawnChance = alien.spawnChance / 10000f,
                    spawnBlockStartIndex = spawnBlockStart,
                    spawnBlockCount = alien.spawnOnBlocks.Length
                };
            }

            int decorationCount = decorationIndex - decorationStartIndex;
            biomeDefinitions[i] = biome.ToBiomeDefinition(decorationStartIndex, decorationCount);

            // Create job-safe definition
            biomeData[i] = new BiomeData
            {
                biomeID = biome.biomeID,
                surfaceBlock = (byte)biome.surfaceBlock,
                subsurfaceBlock = (byte)biome.subsurfaceBlock,
                deepBlock = (byte)biome.deepBlock,
                sedimentBlock = (byte)biome.sedimentBlock,
                gradientThreshold1 = biome.gradientThreshold1,
                gradientThreshold2 = biome.gradientThreshold2,
                decorationStartIndex = decorationStartIndex,
                decorationCount = decorationCount
            };
        }
    }

    // Accessors
    public bool IsInitialized => isInitialized;
    public NativeArray<TerrainTypeData> GetTerrainTypeData() => terrainTypeData;
    public NativeArray<byte> GetTerrainAllowedBiomes() => terrainAllowedBiomes;
    public NativeArray<float> GetCurveData() => allCurveData;
    public NativeArray<BiomeData> GetBiomeData() => biomeData;
    public NativeArray<BiomeDefinition> GetBiomeDefinitions() => biomeDefinitions;
    public NativeArray<DecorationData> GetDecorationData() => decorationData;
    public NativeArray<byte> GetDecorationSpawnBlocks() => decorationSpawnBlocks;
    public int GetWaterLevel() => waterLevel;
    private void OnDestroy()
    {
        if (isInitialized)
        {
            if (terrainTypeData.IsCreated) terrainTypeData.Dispose();
            if (allCurveData.IsCreated) allCurveData.Dispose();
            if (terrainAllowedBiomes.IsCreated) terrainAllowedBiomes.Dispose();
            if (biomeData.IsCreated) biomeData.Dispose();
            if (biomeDefinitions.IsCreated) biomeDefinitions.Dispose();
            if (decorationData.IsCreated) decorationData.Dispose();
            if (decorationSpawnBlocks.IsCreated) decorationSpawnBlocks.Dispose();
        }
    }
}