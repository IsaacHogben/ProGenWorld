#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;

[CustomEditor(typeof(BlockDatabase))]
public class BlockDatabaseEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var blocksProp = serializedObject.FindProperty("blocks");

        EditorGUILayout.LabelField("Block Table", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Sync With Enum"))
        {
            var db = (BlockDatabase)target;
            var enumValues = (BlockType[])Enum.GetValues(typeof(BlockType));

            var newList = new BlockDatabase.BlockEntry[enumValues.Length];
            for (int i = 0; i < enumValues.Length; i++)
            {
                var existing = Array.Find(db.blocks, b => b.type == enumValues[i]);
                newList[i] = existing.name != null
                    ? existing
                    : new BlockDatabase.BlockEntry(enumValues[i]);
            }

            db.blocks = newList;
            EditorUtility.SetDirty(db);
            Debug.Log($"[BlockDatabaseEditor] Synced {enumValues.Length} entries from BlockType enum.");
        }

        if (GUILayout.Button("Add Block"))
        {
            blocksProp.InsertArrayElementAtIndex(blocksProp.arraySize);
        }

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();

        // Draw array entries
        for (int i = 0; i < blocksProp.arraySize; i++)
        {
            var element = blocksProp.GetArrayElementAtIndex(i);
            EditorGUILayout.BeginVertical("box");

            var typeProp = element.FindPropertyRelative("type");
            var nameProp = element.FindPropertyRelative("name");
            var matProp = element.FindPropertyRelative("material");
            var solidProp = element.FindPropertyRelative("isSolid");
            var collProp = element.FindPropertyRelative("hasCollision");
            var transpProp = element.FindPropertyRelative("isTransparent");

            EditorGUILayout.PropertyField(typeProp, new GUIContent("Type"));
            EditorGUILayout.PropertyField(nameProp, new GUIContent("Name"));
            EditorGUILayout.PropertyField(matProp, new GUIContent("Material"));

            EditorGUILayout.BeginHorizontal();
            solidProp.boolValue = EditorGUILayout.ToggleLeft("Solid", solidProp.boolValue);
            collProp.boolValue = EditorGUILayout.ToggleLeft("Collision", collProp.boolValue);
            transpProp.boolValue = EditorGUILayout.ToggleLeft("Transparent", transpProp.boolValue);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
