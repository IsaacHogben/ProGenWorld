using Unity.Collections;
using UnityEngine;

// ============================================================================
// BIOME ENUM
// ============================================================================
public enum BiomeType : byte
{
    Backrooms,
    PineForest,
    Badlands,
    Plains,
    Nightland,
    RedGrass,
    YellowArid,
    Fungus,
    City,
    DarkForest,
    Blight
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

    // Region generator only: the chunk's 2D column data, (chunkSize + 1)^2 at full resolution
    public NativeArray<ColumnSample> columns;

    public void Dispose()
    {
        if (grid.IsCreated) grid.Dispose();
        if (columns.IsCreated) columns.Dispose();
    }
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
    Alien = 3,
    LSystemTree = 4
}

public struct DecorationData
{
    public byte category;           // DecorationCategory
    public byte typeID;             // Specific type within category
    public float spawnChance;       // 0-1 (converted from per ten thousand)
    public int spawnBlockStartIndex;  // Index into spawn blocks array
    public int spawnBlockCount;       // Number of valid spawn blocks
    public float minScale;            // L-system trees: random size per spawn (0 = 1)
    public float maxScale;
}

// ============================================================================
// DECORATION TYPE ENUMS
// ============================================================================

public static class DecorationType
{
    public enum Tree : byte
    {
        SmallPine,
        MediumPine,
        LargePine,
        RedFancy,
        Willow,
        Tallow,
        Palm,
        SmallPalm,
        NightBlossom
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

    // Per column water and decoration (BiomeJob: global water level, scale 1)
    public float waterY;           // water fills air at or below this world y
    public float decoScale;        // decoration spawn chance multiplier
    public byte surfaceOverride;   // block for exposed gentle surfaces (0 = biome palette)

    // Region generator: gradient thresholds inside the column's 3D shapes (world y within the
    // band); elsewhere the primary biome's apply. BiomeJob leaves the band empty.
    public float shapeBandMin, shapeBandMax;
    public float shapeGradient1, shapeGradient2;

    // Region generator: sub-biome layer (RegionDetailSources.Layers index + 1) whose decorations
    // apply in this column; 0 = only the biome's
    public ushort decoLayer;
}

/// <summary>A sub-biome layer's decorations in BiomeDataManager's flattened decoration array.</summary>
public struct SubBiomeDecorations
{
    public int start, count;
    public byte replace;   // 1 = only these spawn where the layer applies, 0 = on top of the biome's
}

// ============================================================================
// SCRIPTABLE OBJECT HELPER CLASSES
// ============================================================================

[System.Serializable]
public class LSystemDecoration
{
    public LSystemScriptableObject LSystemData;

    [Tooltip("Size range for this entry; each tree picks a random scale in it. Add the same tree again with a different range and spawn chance for rare giants.")]
    [Min(0.05f)] public float minScale = 1f;
    [Min(0.05f)] public float maxScale = 1f;

    [Tooltip("Spawn chance per ten thousand blocks (4.5 = 0.45%)")]
    [Range(0f, 10000f)]
    public float spawnChance = 5f;

    [Tooltip("Blocks this tree can spawn on")]
    public BlockType[] spawnOnBlocks = new BlockType[] { BlockType.Grass, BlockType.Dirt };
}
/// <summary>A set of decorations of every kind (a sub-biome layer's own).</summary>
[System.Serializable]
public class DecorationSet
{
    public LSystemDecoration[] lSystems = new LSystemDecoration[0];
    public TreeDecoration[] trees = new TreeDecoration[0];
    public VegetationDecoration[] vegetation = new VegetationDecoration[0];
    public RockDecoration[] rocks = new RockDecoration[0];
    public AlienDecoration[] aliens = new AlienDecoration[0];

    public bool IsEmpty => Count == 0;
    public int Count => (lSystems?.Length ?? 0) + (trees?.Length ?? 0) + (vegetation?.Length ?? 0) + (rocks?.Length ?? 0) + (aliens?.Length ?? 0);
}

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