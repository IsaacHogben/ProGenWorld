using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

[CreateAssetMenu(fileName = "New Terrain Type", menuName = "World Generation/Terrain Type")]
public class TerrainTypeDefinitionSO : ScriptableObject
{
    [Header("Identity")]

    [Tooltip("Display name")]
    public string terrainTypeName = "New Terrain";

    [Header("Noise Configuration")]
    [Tooltip("FastNoise encoded node tree string")]
    [TextArea(3, 10)]
    public string noiseNodeTree = "";

    [Tooltip("Frequency multiplier for this terrain (1.0 = normal, 2.0 = twice as detailed)")]
    [Range(0.01f, 5.0f)]
    public float frequencyMultiplier = 1.0f;

    [Tooltip("Vertical offset for sampling (positive = sample higher, negative = sample lower)")]
    [Range(-500, 500)]
    public int yOffset = 0;

    [Header("Density Curve (Replaced by NodeTree)")]
    [Tooltip("Density modification by height. X = normalized height (0-1), Y = density modifier")]
    public AnimationCurve densityCurve = AnimationCurve.Linear(0, 0, 1, 0);

    [Tooltip("Number of samples to bake from curve")]
    [Range(16, 128)]
    public int curveResolution = 64;

    [Header("Rarity")]
    [Tooltip("Selection weight (higher = more common, 0 = disabled)")]
    [Range(0f, 10f)]
    public float rarityWeight = 1f;

    [Header("Biome Assignment")]
    [Tooltip("Which biomes can appear on this terrain type")]
    public List<BiomeType> allowedBiomes = new List<BiomeType>();

    [Header("Auto-Generated (Read Only)")]
    [SerializeField, ReadOnly]
    private byte terrainID;

    public byte TerrainID => terrainID;

    // Called by BiomeDataManager during initialization
    public void SetTerrainID(byte id)
    {
        terrainID = id;
    }
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
            curveStartIndex = curveStartIndex,
            curveResolution = curveResolution,
            rarityWeight = rarityWeight,
            allowedBiomesStartIndex = allowedBiomesStartIndex,
            allowedBiomesCount = allowedBiomes.Count
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