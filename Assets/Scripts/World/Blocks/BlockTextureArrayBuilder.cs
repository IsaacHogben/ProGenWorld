#if UNITY_EDITOR
using UnityEditor;
#endif
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

    /// <summary>
    /// Per texel relief height (R8, 1 = surface, 0 = deepest), built with the colour array from
    /// each texture's brightness relative to its own darkest / brightest texel. Runtime only.
    /// </summary>
    [System.NonSerialized]
    public Texture2DArray generatedHeights;

    public void GenerateTextureArray()
    {
        Debug.Log("Building texture array from BlockDatabase...");

        if (blockDatabase == null || blockDatabase.blocks == null || blockDatabase.blocks.Length == 0)
        {
            Debug.LogError("BlockDatabase is missing or empty.");
            return;
        }

        int count = blockDatabase.blocks.Length;
        // Mip mapped and filtered: the voxel shader keeps texels crisp up close itself and needs
        // the mips so distant terrain does not shimmer.
        var array = new Texture2DArray(textureSize, textureSize, count, TextureFormat.RGBA32, true)
        {
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
            wrapMode = TextureWrapMode.Repeat
        };

        if (generatedHeights != null)
            Object.DestroyImmediate(generatedHeights);
        var heights = new Texture2DArray(textureSize, textureSize, count, TextureFormat.R8, false)
        {
            name = "BlockReliefHeights",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };
        var heightBytes = new byte[textureSize * textureSize];

        for (int i = 0; i < count; i++)
        {
            var tex = blockDatabase.blocks[i].texture;
            if (tex == null)
            {
                Debug.LogWarning($"Block {i} ('{blockDatabase.blocks[i].name}') has no texture.");
                System.Array.Fill(heightBytes, (byte)255);
                heights.SetPixelData(heightBytes, 0, i);
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

            BuildHeights(converted.GetPixels(), heightBytes);
            heights.SetPixelData(heightBytes, 0, i);

            // --- Copy into array slice (mips are built on Apply) ---
            array.SetPixels32(converted.GetPixels32(), i, 0);
            Object.DestroyImmediate(converted);
        }

        array.Apply(true, true);
        generatedArray = array;
        heights.Apply(false, true);
        generatedHeights = heights;

        Debug.Log($"Generated Texture2DArray with {count} slices ({textureSize}�{textureSize}, RGBA32).");

#if UNITY_EDITOR
        string pathOut = "Assets/Generated/BlockTextureArray.asset";
        System.IO.Directory.CreateDirectory("Assets/Generated");
        AssetDatabase.CreateAsset(array, pathOut);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[BlockTextureArrayBuilder] Saved Texture2DArray to {pathOut}");
#endif
    }

    // Brightness normalised to the texture's own range, so a dark texture is not sunk as a whole.
    // Nearly flat textures stay at the surface.
    static void BuildHeights(Color[] pixels, byte[] heights)
    {
        float min = float.MaxValue, max = float.MinValue;
        var lum = new float[pixels.Length];
        for (int p = 0; p < pixels.Length; p++)
        {
            lum[p] = pixels[p].r * 0.2126f + pixels[p].g * 0.7152f + pixels[p].b * 0.0722f;
            if (pixels[p].a < 0.5f)
                continue;
            min = Mathf.Min(min, lum[p]);
            max = Mathf.Max(max, lum[p]);
        }

        bool flat = max - min < 0.04f;
        for (int p = 0; p < heights.Length; p++)
        {
            float h = flat || p >= lum.Length ? 1f : Mathf.Clamp01((lum[p] - min) / (max - min));
            heights[p] = (byte)Mathf.RoundToInt(h * 255f);
        }
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