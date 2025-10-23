using UnityEditor;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

[CreateAssetMenu(fileName = "BlockTextureArrayBuilder", menuName = "World/Block Texture Array", order = 2)]
public class BlockTextureArrayBuilder : ScriptableObject
{
    public BlockDatabase blockDatabase;
    public int textureSize = 32;
    public bool generateOnPlay = true;

    [HideInInspector]
    public Texture2DArray generatedArray;

    public void GenerateTextureArray()
    {
        Debug.Log("Building texture array from BlockDatabase...");

        if (blockDatabase == null || blockDatabase.blocks == null || blockDatabase.blocks.Length == 0)
        {
            Debug.LogError("BlockDatabase is missing or empty.");
            return;
        }

        int count = blockDatabase.blocks.Length;
        var array = new Texture2DArray(textureSize, textureSize, count, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        for (int i = 0; i < count; i++)
        {
            var tex = blockDatabase.blocks[i].texture;
            if (tex == null)
            {
                Debug.LogWarning($"Block {i} ('{blockDatabase.blocks[i].name}') has no texture.");
                continue;
            }

#if UNITY_EDITOR
            // --- Ensure texture is readable and uncompressed ---
            string path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool modified = false;
                if (!importer.isReadable) { importer.isReadable = true; modified = true; }
                if (importer.textureCompression != TextureImporterCompression.Uncompressed)
                { importer.textureCompression = TextureImporterCompression.Uncompressed; modified = true; }
                if (importer.mipmapEnabled) { importer.mipmapEnabled = false; modified = true; }
                if (modified)
                {
                    importer.SaveAndReimport();
                    Debug.Log($"Reimported {tex.name} as readable/uncompressed.");
                }
            }
#endif

            // --- Force RGBA32 copy safely ---
            Texture2D converted = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            Color[] pixels;

            try
            {
                pixels = tex.GetPixels(0, 0, textureSize, textureSize);
            }
            catch
            {
                // fallback if size mismatch or unreadable region
                pixels = tex.GetPixels();
            }

            converted.SetPixels(pixels);
            converted.Apply();

            // --- Copy into array slice ---
            Graphics.CopyTexture(converted, 0, 0, array, i, 0);
            Object.DestroyImmediate(converted);
        }

        array.Apply(false, true);
        generatedArray = array;

        Debug.Log($"Generated Texture2DArray with {count} slices ({textureSize}×{textureSize}, RGBA32).");

#if UNITY_EDITOR
        string pathOut = "Assets/Generated/BlockTextureArray.asset";
        System.IO.Directory.CreateDirectory("Assets/Generated");
        AssetDatabase.CreateAsset(array, pathOut);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[BlockTextureArrayBuilder] Saved Texture2DArray to {pathOut}");
#endif
    }

    public Texture2DArray GetTextureArray()
    {
        GenerateTextureArray();
        return generatedArray;
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(BlockTextureArrayBuilder))]
public class BlockTextureArrayBuilderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var builder = (BlockTextureArrayBuilder)target;
        if (GUILayout.Button("Generate Texture Array"))
        {
            builder.GenerateTextureArray();
            EditorUtility.SetDirty(builder);
        }
    }
}
#endif