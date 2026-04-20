#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class TextureArrayCreator
{
    [MenuItem("Assets/Create/Texture2D Array")]
    static void CreateTextureArray()
    {
        var array = new Texture2DArray(16, 16, 1, TextureFormat.R8, false);
        array.filterMode = FilterMode.Point;
        array.wrapMode = TextureWrapMode.Repeat;
        array.Apply();

        AssetDatabase.CreateAsset(array, "Assets/NewTextureArray.asset");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = array;
    }
}

[CustomEditor(typeof(Texture2DArray))]
public class Texture2DArrayEditor : Editor
{
    private List<Texture2D> textures = new List<Texture2D>();
    private string StateKey => "Texture2DArrayEditor_" + AssetDatabase.GetAssetPath(target);

    private void OnEnable()
    {
        LoadState();
    }

    private void OnDisable()
    {
        SaveState();
    }

    private void SaveState()
    {
        var guids = new List<string>();
        foreach (var t in textures)
            guids.Add(t != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t)) : "");
        SessionState.SetString(StateKey, string.Join(",", guids));
    }

    private void LoadState()
    {
        textures.Clear();
        var raw = SessionState.GetString(StateKey, "");
        if (string.IsNullOrEmpty(raw)) return;

        var guids = raw.Split(',');
        foreach (var guid in guids)
        {
            if (string.IsNullOrEmpty(guid))
            {
                textures.Add(null);
                continue;
            }
            var path = AssetDatabase.GUIDToAssetPath(guid);
            textures.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        }
    }

    public override void OnInspectorGUI()
    {
        var array = (Texture2DArray)target;

        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        array.filterMode = (FilterMode)EditorGUILayout.EnumPopup("Filter Mode", array.filterMode);
        array.wrapMode = (TextureWrapMode)EditorGUILayout.EnumPopup("Wrap Mode", array.wrapMode);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Texture2D Array", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Slices: {array.depth}  |  Size: {array.width}x{array.height}  |  Format: {array.format}");
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
        for (int i = 0; i < textures.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            textures[i] = (Texture2D)EditorGUILayout.ObjectField($"Slice {i}", textures[i], typeof(Texture2D), false);
            if (GUILayout.Button("-", GUILayout.Width(24)))
            {
                textures.RemoveAt(i);
                SaveState();
                break;
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("+ Add Slot"))
        {
            textures.Add(null);
            SaveState();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Apply"))
        {
            Apply(array);
            SaveState();
        }
    }

    private void Apply(Texture2DArray array)
    {
        if (textures.Count == 0)
        {
            Debug.LogError("[Texture2DArrayEditor] No textures to apply.");
            return;
        }

        Texture2D reference = null;
        foreach (var t in textures)
            if (t != null) { reference = t; break; }

        if (reference == null)
        {
            Debug.LogError("[Texture2DArrayEditor] All slots are empty.");
            return;
        }

        int width = reference.width;
        int height = reference.height;
        int count = textures.Count;

        var newArray = new Texture2DArray(width, height, count, reference.format, true);
        newArray.filterMode = FilterMode.Point;
        newArray.wrapMode = TextureWrapMode.Repeat;
        newArray.name = array.name;

        for (int i = 0; i < count; i++)
        {
            var tex = textures[i];
            if (tex == null)
            {
                Debug.LogWarning($"[Texture2DArrayEditor] Slice {i} is empty, skipping.");
                continue;
            }

            if (tex.width != width || tex.height != height)
            {
                Debug.LogError($"[Texture2DArrayEditor] Slice {i} '{tex.name}' is {tex.width}x{tex.height}, expected {width}x{height}. Aborting.");
                return;
            }

            for (int mip = 0; mip < tex.mipmapCount; mip++)
                Graphics.CopyTexture(tex, 0, mip, newArray, i, mip);
        }

        newArray.Apply(false);
        EditorUtility.CopySerialized(newArray, array);
        EditorUtility.SetDirty(array);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Texture2DArrayEditor] Applied {count} slices ({width}x{height}).");
    }
}
#endif