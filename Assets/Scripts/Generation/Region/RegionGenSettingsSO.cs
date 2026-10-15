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

    [Header("Block Palette Edge")]
    [Tooltip("Where two biomes blend, the block palette switches where their weights cross. This bends that line so it is not smooth.")]
    [Range(0f, 0.5f)] public float paletteEdgeNoise = 0.2f;

    [Tooltip("Size in blocks of the bends in the palette edge.")]
    [Min(4f)] public float paletteEdgeScale = 48f;

    [Header("Density")]
    [Tooltip("Density change per block of height for flat ground. Biome gradient thresholds (0.005 / 0.002) were tuned on FastNoise trees of about 0.01 per block; at 0.01 they switch palette at about 60 and 78 degree slopes.")]
    [Min(0.0001f)] public float densityScale = 0.01f;
}
