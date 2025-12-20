using UnityEngine;

[CreateAssetMenu(fileName = "New Biome", menuName = "World Generation/Biome Definition")]
public class BiomeDefinitionSO : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Unique identifier (0-255)")]
    public byte biomeID;

    [Tooltip("Display name")]
    public string biomeName = "New Biome";

    [Tooltip("Debug visualization color")]
    public Color debugColor = Color.white;

    [Header("Climate Preferences")]
    [Tooltip("Preferred humidity range (0 = dry, 1 = wet)")]
    public Vector2 humidityRange = new Vector2(0f, 1f);

    [Tooltip("Preferred temperature range (0 = cold, 1 = hot)")]
    public Vector2 temperatureRange = new Vector2(0f, 1f);

    [Header("Block Palette")]
    [Tooltip("Surface block for steep slopes (gradient > threshold1)")]
    public BlockType surfaceBlock = BlockType.Grass;

    [Tooltip("Subsurface block for moderate slopes (gradient > threshold2)")]
    public BlockType subsurfaceBlock = BlockType.Dirt;

    [Tooltip("Deep block for gentle slopes (gradient <= threshold2)")]
    public BlockType deepBlock = BlockType.Stone;

    [Tooltip("Sediment block (below water or desert sand)")]
    public BlockType sedimentBlock = BlockType.Dirt;

    [Tooltip("Gradient threshold for surface/subsurface (higher = flatter)")]
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
            biomeID = biomeID,
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
        {
            gradientThreshold2 = gradientThreshold1;
        }

        if (humidityRange.x > humidityRange.y)
        {
            humidityRange = new Vector2(humidityRange.y, humidityRange.x);
        }

        if (temperatureRange.x > temperatureRange.y)
        {
            temperatureRange = new Vector2(temperatureRange.y, temperatureRange.x);
        }
    }
}