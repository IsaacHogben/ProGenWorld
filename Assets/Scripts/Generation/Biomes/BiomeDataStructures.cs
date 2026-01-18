using Unity.Collections;
using UnityEngine;

// ============================================================================
// BIOME ENUM
// ============================================================================
public enum BiomeType : byte
{
    PineForest,
    stone,
    YellowDesrt,
    Plains
}

// ============================================================================
// TERRAIN TYPE DATA
// ============================================================================

public struct TerrainTypeData
{
    public byte terrainTypeID;

    // Curve data
    public int curveStartIndex;    // Index into global curve data array
    public int curveResolution;    // Number of samples

    public float temperatureMin;
    public float temperatureMax;
    public float climateWeight;

    // Elevation preferences
    public float preferredElevation;
    public float elevationTolerance;
    public byte allowsUnderwater;  // 0 or 1
    public byte allowsAboveWater;  // 0 or 1

    // Allowed biomes
    public int allowedBiomesStartIndex;
    public int allowedBiomesCount;

    // Rarity
    public float rarityWeight;
}

// ============================================================================
// BIOME DATA
// ============================================================================

public struct BiomeData
{
    public byte biomeID;

    // Block palette
    public byte surfaceBlock;
    public byte subsurfaceBlock;
    public byte deepBlock;
    public byte sedimentBlock;
    public float gradientThreshold1;
    public float gradientThreshold2;

    // Decoration indices
    public int decorationStartIndex;
    public int decorationCount;

    //For BiomeSystem compatibility
    public int resolution;
    public NativeArray<BiomeHint> grid;
}

// Job-safe biome definition(no nested containers)
public struct BiomeDefinition
{
    public byte biomeID;

    // Block palette
    public byte surfaceBlock;
    public byte subsurfaceBlock;
    public byte deepBlock;
    public byte sedimentBlock;
    public float gradientThreshold1;
    public float gradientThreshold2;

    // Decoration indices
    public int decorationStartIndex;
    public int decorationCount;

    // Climate preferences
    public float humidityMin;
    public float humidityMax;
    public float temperatureMin;
    public float temperatureMax;
}

public struct ClimateGridCell
{
    public int biomeStartIndex;
    public int biomeCount;
}

// ============================================================================
// DECORATION DATA
// ============================================================================

public enum DecorationCategory : byte
{
    Tree = 0,
    Vegetation = 1,
    Rock = 2,
    Alien = 3
}

public struct DecorationData
{
    public byte category;           // DecorationCategory
    public byte typeID;             // Specific type within category
    public float spawnChance;       // 0-1 (converted from per ten thousand)
    public int spawnBlockStartIndex;  // Index into spawn blocks array
    public int spawnBlockCount;       // Number of valid spawn blocks
}

// ============================================================================
// DECORATION TYPE ENUMS
// ============================================================================

public static class DecorationType
{
    public enum Tree : byte
    {
        SmallPine = 0,
        LargePine = 1,
        Oak = 2,
        Birch = 3,
        DeadTree = 4,
        AlienSpire = 5
    }

    public enum Vegetation : byte
    {
        Grass = 0,
        TallGrass = 1,
        Fern = 2,
        Mushroom = 3,
        Flower = 4,
        Bush = 5
    }

    public enum Rock : byte
    {
        SmallBoulder = 0,
        LargeBoulder = 1,
        CrystalCluster = 2,
        Stalagmite = 3
    }

    public enum Alien : byte
    {
        FungalTree = 0,
        GlowingSpore = 1,
        TentaclePlant = 2,
        BioluminescentVine = 3,
        CrystalFormation = 4
    }
}

// ============================================================================
// BIOME HINT (Job Output)
// ============================================================================

public struct BiomeHint
{
    // Terrain shape selection
    public byte primaryTerrainType;
    public byte secondaryTerrainType;
    public byte terrainBlend;  // 0-255

    // Biome/climate selection
    public byte primaryBiome;
    public byte secondaryBiome;
    public byte biomeBlend;    // 0-255
}

// ============================================================================
// SCRIPTABLE OBJECT HELPER CLASSES
// ============================================================================

[System.Serializable]
public class TreeDecoration
{
    public DecorationType.Tree treeType;

    [Tooltip("Spawn chance per ten thousand blocks (4.5 = 0.45%)")]
    [Range(0f, 10000f)]
    public float spawnChance = 5f;

    [Tooltip("Blocks this tree can spawn on")]
    public BlockType[] spawnOnBlocks = new BlockType[] { BlockType.Grass, BlockType.Dirt };
}

[System.Serializable]
public class VegetationDecoration
{
    public DecorationType.Vegetation vegetationType;

    [Tooltip("Spawn chance per thousand blocks")]
    [Range(0f, 1000f)]
    public float spawnChance = 50f;

    [Tooltip("Blocks this vegetation can spawn on")]
    public BlockType[] spawnOnBlocks = new BlockType[] { BlockType.Grass };
}

[System.Serializable]
public class RockDecoration
{
    public DecorationType.Rock rockType;

    [Tooltip("Spawn chance per thousand blocks")]
    [Range(0f, 1000f)]
    public float spawnChance = 10f;

    [Tooltip("Blocks this rock can spawn on")]
    public BlockType[] spawnOnBlocks = new BlockType[] { BlockType.Stone, BlockType.Grass };
}

[System.Serializable]
public class AlienDecoration
{
    public DecorationType.Alien alienType;

    [Tooltip("Spawn chance per thousand blocks")]
    [Range(0f, 1000f)]
    public float spawnChance = 5f;

    [Tooltip("Blocks this alien decoration can spawn on")]
    public BlockType[] spawnOnBlocks = new BlockType[] { BlockType.Grass };
}