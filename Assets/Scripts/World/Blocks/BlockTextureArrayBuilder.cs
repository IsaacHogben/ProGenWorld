#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

[CreateAssetMenu(fileName = "BlockTextureArrayBuilder", menuName = "World/Block Texture Array", order = 2)]
public class BlockTextureArrayBuilder : ScriptableObject
{
    public BlockDatabase blockDatabase;
    public int textureSize = 32;
    public bool generateOnPlay = true;

    /// <summary>
    /// One slice per block, sRGB, mip mapped: the legacy colour texture, or for blocks with a
    /// surface its grey texture stored so that the shader's (linearised) sample returns the
    /// texture's values as they are: r = shade, g = colour position.
    /// </summary>
    [HideInInspector]
    public Texture2DArray generatedArray;

    /// <summary>
    /// Per texel relief height (R8, 1 = surface, 0 = deepest): the surface's painted relief mask,
    /// or each texture's brightness relative to its own darkest / brightest texel. Runtime only.
    /// </summary>
    [System.NonSerialized]
    public Texture2DArray generatedHeights;

    /// <summary>
    /// Alpha masks (R8): the legacy Resources/VoxelAlphaMasks slices followed by the surfaces'
    /// masks, in BlockDatabase.CollectAlphaMasks order. Runtime only.
    /// </summary>
    [System.NonSerialized]
    public Texture2DArray generatedAlphaMasks;

    /// <summary>
    /// Colour lookup per block (slice = block id; linear RGB, half floats): BlockSurface.Colour
    /// by (x: palette position, y: shade). Runtime only.
    /// </summary>
    [System.NonSerialized]
    public Texture2DArray generatedSurfaceLut;

    /// <summary>BlockSurface.PatchParams per block id (256 entries). Runtime only.</summary>
    [System.NonSerialized]
    public Vector4[] generatedSurfaceParams;

    public const int SurfaceLutSize = 64;

    /// <param name="saveAsset">Also write the colour array to Assets/Generated (editor only).</param>
    public void GenerateTextureArray(bool saveAsset = true)
    {
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
            var entry = blockDatabase.blocks[i];
            var surface = entry.surface;
            var tex = surface != null ? surface.greyTexture : entry.texture;
            if (tex == null)
            {
                if (entry.type != BlockType.Air)
                    Debug.LogWarning($"Block {i} ('{entry.type}') has no {(surface != null ? "grey texture" : "texture")}.");
                System.Array.Fill(heightBytes, (byte)255);
                heights.SetPixelData(heightBytes, 0, i);
                continue;
            }

            var pixels = ReadPixels(tex, textureSize);
            Color[] shade = pixels;
            if (surface != null)
            {
                shade = new Color[pixels.Length];
                for (int p = 0; p < pixels.Length; p++)
                {
                    float r = pixels[p].r, g = pixels[p].g;
                    shade[p] = new Color(r, r, r, 1f);
                    // Pre-encoded so the sRGB sample comes back as the plain value (filtered linearly).
                    float er = Mathf.LinearToGammaSpace(r), eg = Mathf.LinearToGammaSpace(g);
                    pixels[p] = new Color(er, eg, er, 1f);
                }
            }

            if (surface != null && surface.reliefMask != null)
            {
                var relief = ReadPixels(surface.reliefMask, textureSize);
                for (int p = 0; p < heightBytes.Length; p++)
                    heightBytes[p] = (byte)Mathf.RoundToInt(Mathf.Clamp01(relief[p].r) * 255f);
            }
            else
                BuildHeights(shade, heightBytes);
            heights.SetPixelData(heightBytes, 0, i);

            array.SetPixels(pixels, i, 0);
        }

        array.Apply(true, true);
        if (generatedArray != null && generatedArray != array && !EditorAsset(generatedArray))
            Object.DestroyImmediate(generatedArray);
        generatedArray = array;
        heights.Apply(false, true);
        generatedHeights = heights;

        BuildAlphaMasks();
        UpdateSurfaceLut();

        Debug.Log($"Generated block texture array with {count} slices ({textureSize}x{textureSize}).");

#if UNITY_EDITOR
        if (saveAsset)
        {
            string pathOut = "Assets/Generated/BlockTextureArray.asset";
            System.IO.Directory.CreateDirectory("Assets/Generated");
            AssetDatabase.CreateAsset(array, pathOut);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
#endif
    }

    static bool EditorAsset(Object o)
    {
#if UNITY_EDITOR
        return AssetDatabase.Contains(o);
#else
        return false;
#endif
    }

    /// <summary>
    /// Bakes every surface's colours into the lookup texture and its patch settings into
    /// generatedSurfaceParams. Cheap enough to call on every colour edit; keeps the same texture.
    /// </summary>
    public void UpdateSurfaceLut()
    {
        int count = blockDatabase.blocks.Length;
        const int n = SurfaceLutSize;
        if (generatedSurfaceLut == null || generatedSurfaceLut.depth != count)
        {
            if (generatedSurfaceLut != null)
                Object.DestroyImmediate(generatedSurfaceLut);
            generatedSurfaceLut = new Texture2DArray(n, n, count, TextureFormat.RGBAHalf, false, true)
            {
                name = "BlockSurfaceLut",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }
        generatedSurfaceParams ??= new Vector4[256];

        var lut = new Color[n * n];
        for (int b = 0; b < count; b++)
        {
            var surface = blockDatabase.blocks[b].surface;
            if (surface == null)
                continue;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    lut[x + y * n] = surface.Colour(x / (n - 1f), y / (n - 1f)).linear;
            generatedSurfaceLut.SetPixels(lut, b);
            if (b < generatedSurfaceParams.Length)
                generatedSurfaceParams[b] = surface.PatchParams;
        }
        generatedSurfaceLut.Apply(false, false);
    }

    void BuildAlphaMasks()
    {
        if (generatedAlphaMasks != null)
            Object.DestroyImmediate(generatedAlphaMasks);
        generatedAlphaMasks = null;

        var legacy = BlockDatabase.LegacyAlphaMasks;
        var masks = blockDatabase.CollectAlphaMasks(out _);
        int size = legacy != null ? legacy.width : 16;
        int legacyCount = legacy != null ? legacy.depth : 1;

        var array = new Texture2DArray(size, size, legacyCount + masks.Count, TextureFormat.R8, false)
        {
            name = "BlockAlphaMasks",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        var opaque = new Color[size * size];
        System.Array.Fill(opaque, Color.white);
        for (int s = 0; s < legacyCount; s++)
        {
            Color[] slice = opaque;
            if (legacy != null && legacy.isReadable)
                slice = legacy.GetPixels(s);
            else if (s > 0)
                Debug.LogWarning("[BlockTextureArrayBuilder] Resources/VoxelAlphaMasks is not readable: legacy alpha masks render opaque.");
            array.SetPixels(slice, s);
        }
        for (int m = 0; m < masks.Count; m++)
            array.SetPixels(ReadPixels(masks[m], size), legacyCount + m);

        array.Apply(false, true);
        generatedAlphaMasks = array;
    }

    /// <summary>
    /// Pixels of a texture at size x size (nearest neighbour when the texture has another size).
    /// In the editor, makes the texture readable and uncompressed first.
    /// </summary>
    public static Color[] ReadPixels(Texture2D tex, int size)
    {
#if UNITY_EDITOR
        string path = AssetDatabase.GetAssetPath(tex);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
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
        var src = tex.GetPixels();
        if (tex.width == size && tex.height == size)
            return src;

        var dst = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                dst[x + y * size] = src[x * tex.width / size + (y * tex.height / size) * tex.width];
        return dst;
    }

    /// <summary>
    /// Relief heights from brightness normalised to the texture's own range, so a dark texture is
    /// not sunk as a whole. Nearly flat textures stay at the surface.
    /// </summary>
    public static void BuildHeights(Color[] pixels, byte[] heights)
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
