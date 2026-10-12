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

        if (GUILayout.Button("Open Block Studio", GUILayout.Height(28f)))
            BlockStudioWindow.OpenFor((BlockDatabase)target);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Block Table", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("Sync With Enum"))
        {
            serializedObject.ApplyModifiedProperties();
            BlockStudioWindow.SyncWithEnum((BlockDatabase)target, -1);
            serializedObject.Update();
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
            var surfaceProp = element.FindPropertyRelative("surface");
            var matProp = element.FindPropertyRelative("texture");
            var solidProp = element.FindPropertyRelative("isSolid");
            var collProp = element.FindPropertyRelative("hasCollision");
            var visProp = element.FindPropertyRelative("visibility");
            var alphaMaskProp = element.FindPropertyRelative("alphaMaskIndex");
            var decorationProp = element.FindPropertyRelative("surfaceDecoration");

            EditorGUILayout.PropertyField(typeProp, new GUIContent("Type"));
            EditorGUILayout.PropertyField(surfaceProp, new GUIContent("Surface"));
            if (surfaceProp.objectReferenceValue == null)
                EditorGUILayout.PropertyField(matProp, new GUIContent("Texture"));
            EditorGUILayout.PropertyField(visProp, new GUIContent("Visibility"));
            if ((BlockVisibility)visProp.enumValueIndex == BlockVisibility.Stacked && surfaceProp.objectReferenceValue == null)
            {
                EditorGUILayout.PropertyField(alphaMaskProp, new GUIContent("Alpha Mask Index"));
            }
            EditorGUILayout.PropertyField(decorationProp, new GUIContent("Surface Decoration"));
            EditorGUILayout.BeginHorizontal();

            solidProp.boolValue = EditorGUILayout.ToggleLeft("Solid", solidProp.boolValue);
            collProp.boolValue = EditorGUILayout.ToggleLeft("Collision", collProp.boolValue);
            EditorGUILayout.EndHorizontal();

            



            EditorGUILayout.EndVertical();
        }

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
