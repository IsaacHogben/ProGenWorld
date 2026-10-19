using UnityEngine;

/// <summary>
/// World wide settings of the region generator (ChunkManager > Use Region Gen): the size and shape
/// of biome regions, how wide their borders blend, the large scale height every biome sits on, and
/// how density is scaled. Per biome shapes live on each BiomeDefinitionSO (Region Shape).
/// </summary>
[CreateAssetMenu(fileName = "RegionGenSettings", menuName = "World Generation/Region Gen Settings")]
public class RegionGenSettingsSO : ScriptableObject
{
    [Header("Regions")]
    [Tooltip("Average distance between region centres in blocks. Each region is one biome.")]
    [Min(500f)] public float regionSize = 12000f;

    [Tooltip("How far region centres stray from a regular grid (0 = square grid, 1 = anywhere in their cell).")]
    [Range(0f, 1f)] public float regionJitter = 0.85f;

    [Tooltip("How far region borders are bent, as a fraction of the region size.")]
    [Range(0f, 1f)] public float borderWarp = 0.3f;

    [Tooltip("Size of the bends in region borders, as a fraction of the region size.")]
    [Range(0.05f, 2f)] public float borderWarpScale = 0.5f;

    [Tooltip("Width in blocks over which neighbouring regions' shapes blend into each other.")]
    [Min(1f)] public float borderBlend = 1200f;

    [Header("Climate")]
    [Tooltip("Size in blocks of climate (temperature / humidity) features. Regions pick their biome from the climate at their centre.")]
    [Min(100f)] public float climateScale = 40000f;

    [Tooltip("Spreads climate values towards the edges of the climate grid (1 = raw noise, which rarely reaches the corner cells).")]
    [Range(0.5f, 3f)] public float climateContrast = 1.6f;

    [Header("Macro Height")]
    [Tooltip("Height every biome sits on, in blocks.")]
    public float macroBase = 0f;

    [Tooltip("Height of continent scale rises and dips added under every biome, in blocks.")]
    [Min(0f)] public float macroAmplitude = 40f;

    [Tooltip("Size of the continent scale rises and dips, in blocks.")]
    [Min(100f)] public float macroScale = 8000f;

    [Header("3D Shapes")]
    [Tooltip("3D shape weights below this fade to nothing and the tree is not sampled there (the fade runs up to twice this). The trees are by far the costliest part of generation: higher loads faster, but soft sub-biome edges lose their faint shapes.")]
    [Range(0f, 0.3f)] public float minShapeWeight = 0.05f;

    [Header("Block Palette Edge")]
    [Tooltip("Where two biomes blend, the block palette switches where their weights cross. This bends that line so it is not smooth.")]
    [Range(0f, 0.5f)] public float paletteEdgeNoise = 0.2f;

    [Tooltip("Size in blocks of the bends in the palette edge.")]
    [Min(4f)] public float paletteEdgeScale = 48f;

    [Header("Rivers")]
    [Tooltip("Carve rivers and lakes. Rivers run between drainage nodes, each draining to its lowest neighbour; a node with no lower neighbour collects a lake.")]
    public bool rivers = false;

    [Tooltip("Spacing of drainage nodes in blocks. Smaller follows the terrain more closely but bends rivers more often.")]
    [Range(64f, 1024f)] public float riverNodeSpacing = 256f;

    [Tooltip("Drainage nodes per river tile side (tiles are planned one at a time, in the background).")]
    [Range(16, 128)] public int riverTileNodes = 64;

    [Tooltip("How far upstream (in nodes) rainfall is gathered. Larger lets rivers grow further; width stops growing past this catchment.")]
    [Range(4, 48)] public int riverCatchmentRadius = 24;

    [Tooltip("Rainfall a node must gather before a river forms (each node adds 0.3 to 1.5 by humidity and biome rainfall). Lower = more, smaller rivers.")]
    [Min(1f)] public float riverThreshold = 14f;

    [Tooltip("River width growth: width = min width + this * sqrt(rainfall over the threshold).")]
    [Min(0f)] public float riverWidthScale = 1.6f;

    [Min(1f)] public float riverMinWidth = 3f;
    [Min(1f)] public float riverMaxWidth = 40f;

    [Tooltip("Channel depth below the water surface per block of width (plus 1).")]
    [Range(0f, 0.5f)] public float riverDepthPerWidth = 0.12f;

    [Tooltip("Width of the valley carved beside a river, in blocks: this plus Valley Per Width times the river width.")]
    [Min(0f)] public float valleyBase = 24f;
    [Min(0f)] public float valleyPerWidth = 4f;

    [Tooltip("How far rivers wander from the straight line between nodes, as a fraction of the node spacing.")]
    [Range(0f, 0.5f)] public float riverMeander = 0.2f;

    [Tooltip("Width in blocks of the sediment (sand / gravel) banks either side of the water.")]
    [Range(0f, 12f)] public float riverBankWidth = 3f;

    [Header("Lakes")]
    [Tooltip("Rainfall a basin must gather to hold a lake. Drier basins stay dry.")]
    [Min(0f)] public float lakeMinRainfall = 6f;

    [Tooltip("Lake radius per square root of gathered rainfall, as a fraction of the node spacing.")]
    [Range(0.01f, 1f)] public float lakeSize = 0.12f;

    [Tooltip("Largest lake radius, as a fraction of the node spacing.")]
    [Range(0.1f, 1.5f)] public float lakeMaxRadius = 0.9f;

    [Tooltip("Width in blocks of the shore raised around a lake so its water cannot spill over low ground.")]
    [Range(2f, 64f)] public float lakeShore = 16f;

    [Header("Density")]
    [Tooltip("Density change per block of height for flat ground. Biome gradient thresholds (0.005 / 0.002) were tuned on FastNoise trees of about 0.01 per block; at 0.01 they switch palette at about 60 and 78 degree slopes.")]
    [Min(0.0001f)] public float densityScale = 0.01f;
}
