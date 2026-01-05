// ============================================================================
// BIOME DEFINITION SO - Add rarity weight
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

[CreateAssetMenu(fileName = "New Biome", menuName = "World Generation/Biome Definition")]
public class BiomeDefinitionSO : ScriptableObject
{
    [Header("Biome Identity")]
    public BiomeType biomeType;
    public byte BiomeID => (byte)biomeType;

    [Header("Biome Rarity")]
    [Tooltip("Higher = more common when multiple biomes compete (1.0 = normal)")]
    [Range(0.1f, 10f)]
    public float rarityWeight = 1.0f;

    [Header("Climate Preferences (DEPRECATED - Use ClimateGrid instead)")]
    [Tooltip("These ranges are only used if no ClimateGrid is assigned")]
    public Vector2 humidityRange = new Vector2(0f, 1f);
    [Tooltip("These ranges are only used if no ClimateGrid is assigned")]
    public Vector2 temperatureRange = new Vector2(0f, 1f);

    [Header("Block Palette")]
    [Tooltip("Surface block for steep slopes")]
    public BlockType surfaceBlock = BlockType.Grass;
    [Tooltip("Subsurface block for moderate slopes")]
    public BlockType subsurfaceBlock = BlockType.Dirt;
    [Tooltip("Deep block for gentle slopes")]
    public BlockType deepBlock = BlockType.Stone;
    [Tooltip("Sediment block (below water or desert sand)")]
    public BlockType sedimentBlock = BlockType.Dirt;
    [Tooltip("Gradient threshold for surface/subsurface")]
    [Range(0f, 0.1f)]
    public float gradientThreshold1 = 0.005f;
    [Tooltip("Gradient threshold for subsurface/deep")]
    [Range(0f, 0.1f)]
    public float gradientThreshold2 = 0.002f;

    [Header("Tree Decorations")]
    public TreeDecoration[] trees = new TreeDecoration[0];
    [Header("Vegetation Decorations")]
    public VegetationDecoration[] vegetation = new VegetationDecoration[0];
    [Header("Rock Decorations")]
    public RockDecoration[] rocks = new RockDecoration[0];
    [Header("Alien Decorations")]
    public AlienDecoration[] aliens = new AlienDecoration[0];

    public BiomeDefinition ToBiomeDefinition(int decorationStartIndex, int decorationCount)
    {
        return new BiomeDefinition
        {
            biomeID = BiomeID,
            surfaceBlock = (byte)surfaceBlock,
            subsurfaceBlock = (byte)subsurfaceBlock,
            deepBlock = (byte)deepBlock,
            sedimentBlock = (byte)sedimentBlock,
            gradientThreshold1 = gradientThreshold1,
            gradientThreshold2 = gradientThreshold2,
            decorationStartIndex = decorationStartIndex,
            decorationCount = decorationCount,
            humidityMin = humidityRange.x,
            humidityMax = humidityRange.y,
            temperatureMin = temperatureRange.x,
            temperatureMax = temperatureRange.y
        };
    }

    private void OnValidate()
    {
        if (gradientThreshold2 > gradientThreshold1)
            gradientThreshold2 = gradientThreshold1;
        if (humidityRange.x > humidityRange.y)
            humidityRange = new Vector2(humidityRange.y, humidityRange.x);
        if (temperatureRange.x > temperatureRange.y)
            temperatureRange = new Vector2(temperatureRange.y, temperatureRange.x);
    }
}