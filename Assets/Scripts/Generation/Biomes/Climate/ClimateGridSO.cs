using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class ClimateCell
{
    [Tooltip("Biomes that can appear in this climate zone")]
    public List<BiomeType> biomes = new List<BiomeType>();
}

[CreateAssetMenu(fileName = "ClimateGrid", menuName = "World Generation/Climate Grid")]
public class ClimateGridSO : ScriptableObject
{
    [Header("Grid Configuration")]
    [Tooltip("Number of humidity divisions (columns)")]
    [Range(2, 10)]
    public int humidityDivisions = 3;

    [Tooltip("Number of temperature divisions (rows)")]
    [Range(2, 10)]
    public int temperatureDivisions = 3;

    [Header("Fallback")]
    [Tooltip("Biome to use when no match is found")]
    public BiomeType defaultBiome = BiomeType.PineForest;

    [Header("Climate Cells")]
    [Tooltip("Grid cells from bottom-left to top-right (row-major)")]
    public List<ClimateCell> cells = new List<ClimateCell>();

    public void OnValidate()
    {
        // Ensure cells array matches grid size
        int requiredCells = humidityDivisions * temperatureDivisions;

        while (cells.Count < requiredCells)
            cells.Add(new ClimateCell());

        while (cells.Count > requiredCells)
            cells.RemoveAt(cells.Count - 1);
    }

    public ClimateCell GetCell(int humidityIndex, int tempIndex)
    {
        int index = tempIndex * humidityDivisions + humidityIndex;
        if (index >= 0 && index < cells.Count)
            return cells[index];
        return null;
    }

    public ClimateCell GetCellFromClimate(float humidity, float temperature)
    {
        int humidityIndex = Mathf.Clamp(
            Mathf.FloorToInt(humidity * humidityDivisions),
            0,
            humidityDivisions - 1
        );

        int tempIndex = Mathf.Clamp(
            Mathf.FloorToInt(temperature * temperatureDivisions),
            0,
            temperatureDivisions - 1
        );

        return GetCell(humidityIndex, tempIndex);
    }

    public string GetCellLabel(int humidityIndex, int tempIndex)
    {
        string[] humidityLabels = { "Dry", "Moderate", "Wet", "Very Wet" };
        string[] tempLabels = { "Cold", "Cool", "Warm", "Hot", "Very Hot" };

        string humLabel = humidityIndex < humidityLabels.Length
            ? humidityLabels[humidityIndex]
            : $"H{humidityIndex}";

        string tempLabel = tempIndex < tempLabels.Length
            ? tempLabels[tempIndex]
            : $"T{tempIndex}";

        return $"{tempLabel} + {humLabel}";
    }
}