using UnityEngine;

[CreateAssetMenu(fileName = "New Terrain Type", menuName = "World Generation/Terrain Type")]
public class TerrainTypeDefinitionSO : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Unique identifier (0-255)")]
    public byte terrainTypeID;

    [Tooltip("Display name")]
    public string terrainTypeName = "New Terrain";

    [Tooltip("Debug visualization color")]
    public Color debugColor = Color.white;

    [Header("Density Curve")]
    [Tooltip("Density modification by height. X = normalized height (0-1), Y = density modifier")]
    public AnimationCurve densityCurve = AnimationCurve.Linear(0, 0, 1, 0);

    [Tooltip("Number of samples to bake from curve")]
    [Range(16, 128)]
    public int curveResolution = 64;

    [Header("Elevation Preferences")]
    [Tooltip("Preferred elevation relative to water level (-1 = deep, 0 = water level, 1 = high)")]
    [Range(-1f, 1f)]
    public float preferredElevation = 0f;

    [Tooltip("How far from preferred elevation this can exist")]
    [Range(0f, 2f)]
    public float elevationTolerance = 1f;

    [Tooltip("Can exist underwater?")]
    public bool allowsUnderwater = false;

    [Tooltip("Can exist above water?")]
    public bool allowsAboveWater = true;

    [Header("Rarity")]
    [Tooltip("Selection weight (higher = more common, 0 = disabled)")]
    [Range(0f, 10f)]
    public float rarityWeight = 1f;

    [Header("Biome Assignment")]
    [Tooltip("Which biomes can appear on this terrain type")]
    public byte[] allowedBiomes = new byte[0];

    public float[] BakeCurve()
    {
        float[] samples = new float[curveResolution];
        for (int i = 0; i < curveResolution; i++)
        {
            float t = i / (float)(curveResolution - 1);
            samples[i] = densityCurve.Evaluate(t);
        }
        return samples;
    }

    public TerrainTypeData ToTerrainTypeData(int curveStartIndex, int allowedBiomesStartIndex)
    {
        return new TerrainTypeData
        {
            terrainTypeID = terrainTypeID,
            curveStartIndex = curveStartIndex,
            curveResolution = curveResolution,
            preferredElevation = preferredElevation,
            elevationTolerance = elevationTolerance,
            allowsUnderwater = allowsUnderwater ? (byte)1 : (byte)0,
            allowsAboveWater = allowsAboveWater ? (byte)1 : (byte)0,
            rarityWeight = rarityWeight,
            allowedBiomesStartIndex = allowedBiomesStartIndex,
            allowedBiomesCount = allowedBiomes.Length
        };
    }

    private void OnValidate()
    {
        if (densityCurve == null || densityCurve.keys.Length == 0)
        {
            densityCurve = AnimationCurve.Linear(0, 0, 1, 0);
        }
    }
}