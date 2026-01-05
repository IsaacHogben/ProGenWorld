#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ClimateGridSO))]
public class ClimateGridEditor : Editor
{
    private Vector2 scrollPos;

    public override void OnInspectorGUI()
    {
        ClimateGridSO grid = (ClimateGridSO)target;

        EditorGUI.BeginChangeCheck();

        // Header
        EditorGUILayout.LabelField("Climate Grid Configuration", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Grid size controls
        grid.humidityDivisions = EditorGUILayout.IntSlider("Humidity Divisions (Columns)", grid.humidityDivisions, 2, 10);
        grid.temperatureDivisions = EditorGUILayout.IntSlider("Temperature Divisions (Rows)", grid.temperatureDivisions, 2, 10);

        EditorGUILayout.Space();

        // Default biome
        grid.defaultBiome = (BiomeType)EditorGUILayout.EnumPopup("Default Biome", grid.defaultBiome);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Climate Grid", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Grid is arranged with Temperature as rows (top = hot, bottom = cold) and Humidity as columns (left = dry, right = wet).",
            MessageType.Info
        );

        if (EditorGUI.EndChangeCheck())
        {
            grid.OnValidate();
            EditorUtility.SetDirty(grid);
        }

        EditorGUILayout.Space();

        // Draw grid
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        DrawClimateGrid(grid);
        EditorGUILayout.EndScrollView();

        if (GUI.changed)
            EditorUtility.SetDirty(grid);
    }

    private void DrawClimateGrid(ClimateGridSO grid)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // Draw from top to bottom (hot to cold)
        for (int tempIndex = grid.temperatureDivisions - 1; tempIndex >= 0; tempIndex--)
        {
            EditorGUILayout.BeginHorizontal();

            // Row label
            GUILayout.Label(GetTempLabel(tempIndex), GUILayout.Width(60));

            // Draw cells left to right (dry to wet)
            for (int humIndex = 0; humIndex < grid.humidityDivisions; humIndex++)
            {
                DrawCell(grid, humIndex, tempIndex);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);
        }

        // Column labels
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(65);
        for (int humIndex = 0; humIndex < grid.humidityDivisions; humIndex++)
        {
            GUILayout.Label(GetHumidityLabel(humIndex), GUILayout.Width(100));
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void DrawCell(ClimateGridSO grid, int humIndex, int tempIndex)
    {
        ClimateCell cell = grid.GetCell(humIndex, tempIndex);
        if (cell == null) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(200));

        // Cell label
        string label = grid.GetCellLabel(humIndex, tempIndex);
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

        // Biomes list
        SerializedObject so = new SerializedObject(grid);
        int cellIndex = tempIndex * grid.humidityDivisions + humIndex;
        SerializedProperty cellProp = so.FindProperty("cells").GetArrayElementAtIndex(cellIndex);
        SerializedProperty biomesProp = cellProp.FindPropertyRelative("biomes");

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(biomesProp, GUIContent.none, true);
        if (EditorGUI.EndChangeCheck())
        {
            so.ApplyModifiedProperties();
        }

        // Show biome count
        if (cell.biomes.Count > 0)
        {
            EditorGUILayout.LabelField($"({cell.biomes.Count} biomes)", EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("(empty - will use default)", EditorStyles.miniLabel);
        }

        EditorGUILayout.EndVertical();
    }

    private string GetTempLabel(int index)
    {
        string[] labels = { "Cold", "Cool", "Warm", "Hot", "V.Hot" };
        return index < labels.Length ? labels[index] : $"T{index}";
    }

    private string GetHumidityLabel(int index)
    {
        string[] labels = { "Dry", "Moderate", "Wet", "Very Wet" };
        return index < labels.Length ? labels[index] : $"H{index}";
    }
}
#endif