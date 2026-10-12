#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates, duplicates and converts BlockSurface assets and their textures. Every surface lives
/// in its own folder (Assets/Blocks/Surfaces/Name/) next to the PNGs it owns: Name_Grey,
/// Name_Relief and Name_Alpha. Painting a texture that belongs to another surface (a duplicated
/// asset still pointing at the original's PNGs) writes a new PNG in this surface's folder.
/// </summary>
public static class BlockSurfaceAssets
{
    public const string Root = "Assets/Blocks/Surfaces";
    public const string SharedMaskFolder = "Assets/Blocks/AlphaMasks";

    public enum Kind { Grey, Relief, Alpha }

    // ------------------------------------------------------------------
    // Paths and PNG files
    // ------------------------------------------------------------------

    public static string FolderOf(BlockSurface surface)
        => Path.GetDirectoryName(AssetDatabase.GetAssetPath(surface)).Replace('\\', '/');

    public static string OwnedPath(BlockSurface surface, Kind kind)
        => $"{FolderOf(surface)}/{surface.name}_{kind}.png";

    static string UniqueFolder(string name)
    {
        EnsureFolder(Root);
        string folder = $"{Root}/{name}";
        for (int i = 2; AssetDatabase.IsValidFolder(folder) || File.Exists(folder + ".asset"); i++)
            folder = $"{Root}/{name}{i}";
        EnsureFolder(folder);
        return folder;
    }

    public static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    /// <summary>Writes a square PNG (rgb = value, opaque) and imports it as a readable point filtered texture.</summary>
    public static Texture2D WriteGreyPng(string path, float[] values, int size)
    {
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            float v = Mathf.Clamp01(values[i]);
            pixels[i] = new Color(v, v, v, 1f);
        }
        return WritePng(path, pixels, size);
    }

    public static Texture2D WritePng(string path, Color[] pixels, int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>Saves painted values as the surface's own PNG of that kind and assigns it.</summary>
    public static Texture2D SaveOwned(BlockSurface surface, Kind kind, float[] values, int size)
    {
        var tex = WriteGreyPng(OwnedPath(surface, kind), values, size);
        Assign(surface, kind, tex);
        return tex;
    }

    /// <summary>Saves pixels as the surface's own PNG of that kind and assigns it.</summary>
    public static Texture2D SaveOwnedPixels(BlockSurface surface, Kind kind, Color[] pixels, int size)
    {
        var tex = WritePng(OwnedPath(surface, kind), pixels, size);
        Assign(surface, kind, tex);
        return tex;
    }

    static void Assign(BlockSurface surface, Kind kind, Texture2D tex)
    {
        Undo.RecordObject(surface, $"Paint {kind}");
        switch (kind)
        {
            case Kind.Grey: surface.greyTexture = tex; break;
            case Kind.Relief: surface.reliefMask = tex; break;
            case Kind.Alpha: surface.alphaMask = tex; break;
        }
        EditorUtility.SetDirty(surface);
    }

    public static float[] ReadRed(Texture2D tex, int size)
    {
        var pixels = BlockTextureArrayBuilder.ReadPixels(tex, size);
        var values = new float[pixels.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = pixels[i].r;
        return values;
    }

    // ------------------------------------------------------------------
    // Create / duplicate
    // ------------------------------------------------------------------

    static BlockSurface CreateAsset(string name, out string folder)
    {
        folder = UniqueFolder(name);
        name = Path.GetFileName(folder);
        var surface = ScriptableObject.CreateInstance<BlockSurface>();
        surface.name = name;
        AssetDatabase.CreateAsset(surface, $"{folder}/{name}.asset");
        return surface;
    }

    /// <summary>A new surface with a tileable noise grey texture and a single grey colour.</summary>
    public static BlockSurface CreateNew(string name, int size)
    {
        var surface = CreateAsset(name, out _);
        surface.colours = new[] { new Color(0.55f, 0.55f, 0.55f, 1f) };
        surface.greyTexture = WriteGreyPng(OwnedPath(surface, Kind.Grey), TileableNoise(size, 4, 1234), size);
        EditorUtility.SetDirty(surface);
        AssetDatabase.SaveAssets();
        return surface;
    }

    /// <summary>A copy of the surface with copies of the PNGs it owns (shared masks stay shared).</summary>
    public static BlockSurface Duplicate(BlockSurface source, string name)
    {
        var copy = CreateAsset(name, out _);
        string copyName = copy.name;
        EditorUtility.CopySerialized(source, copy);
        copy.name = copyName;

        string sourceFolder = FolderOf(source);
        copy.greyTexture = CopyIfOwned(source.greyTexture, sourceFolder, copy, Kind.Grey);
        copy.reliefMask = CopyIfOwned(source.reliefMask, sourceFolder, copy, Kind.Relief);
        copy.alphaMask = CopyIfOwned(source.alphaMask, sourceFolder, copy, Kind.Alpha);
        EditorUtility.SetDirty(copy);
        AssetDatabase.SaveAssets();
        return copy;
    }

    static Texture2D CopyIfOwned(Texture2D tex, string sourceFolder, BlockSurface copy, Kind kind)
    {
        if (tex == null)
            return null;
        string path = AssetDatabase.GetAssetPath(tex);
        if (Path.GetDirectoryName(path).Replace('\\', '/') != sourceFolder)
            return tex;
        string target = OwnedPath(copy, kind);
        AssetDatabase.CopyAsset(path, target);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(target);
    }

    public static float[] TileableNoise(int size, int cells, int seed)
    {
        var rng = new System.Random(seed);
        var lattice = new float[cells * cells];
        for (int i = 0; i < lattice.Length; i++)
            lattice[i] = (float)rng.NextDouble();

        var values = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = (float)x / size * cells, fy = (float)y / size * cells;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = Mathf.SmoothStep(0f, 1f, fx - x0), ty = Mathf.SmoothStep(0f, 1f, fy - y0);
                float L(int cx, int cy) => lattice[(cx % cells) + (cy % cells) * cells];
                float v = Mathf.Lerp(Mathf.Lerp(L(x0, y0), L(x0 + 1, y0), tx), Mathf.Lerp(L(x0, y0 + 1), L(x0 + 1, y0 + 1), tx), ty);
                values[x + y * size] = Mathf.Clamp01(v * 0.75f + (float)rng.NextDouble() * 0.25f);
            }
        return values;
    }

    // ------------------------------------------------------------------
    // Conversion from a full colour texture
    // ------------------------------------------------------------------

    public struct Fit
    {
        public Color[] colours;
        public float shadeDark, shadeLight;
        public float[] shade;      // per texel, grey texture red
        public float[] position;   // per texel, grey texture green
        public float error;        // RMS per channel (0..1) of the fitted texels against the texture
    }

    static readonly Vector3 LumaWeights = new Vector3(0.2126f, 0.7152f, 0.0722f);

    static void RampWeights(float t, int n, float[] w)
    {
        if (n == 1) { w[0] = 1f; return; }
        if (n == 2) { w[0] = 1f - t; w[1] = t; return; }
        w[0] = Mathf.Clamp01(1f - 2f * t);
        w[2] = Mathf.Clamp01(2f * t - 1f);
        w[1] = 1f - w[0] - w[2];
    }

    static Color Ramp(Color[] colours, float t, float[] w)
    {
        RampWeights(t, colours.Length, w);
        Color c = Color.clear;
        for (int k = 0; k < colours.Length; k++)
            c += colours[k] * w[k];
        return c;
    }

    static float Dist2(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>
    /// Fits a palette of n colours, a linear shade, and a shade and palette position per texel so
    /// that Ramp(position) * Shade(shade) reproduces the texture (BlockSurface.Colour with texel
    /// colour variation 1 and neutral patches). Starts from brightness for both, then alternates
    /// between solving the colours and moving each texel's position and shade to their best fit,
    /// so hue changes at the same brightness are kept.
    /// </summary>
    public static Fit FitPalette(Color[] pixels, int n, int iterations = 10)
    {
        int count = pixels.Length;
        var lum = new float[count];
        float lMin = float.MaxValue, lMax = float.MinValue, lMean = 0f;
        for (int i = 0; i < count; i++)
        {
            lum[i] = pixels[i].r * LumaWeights.x + pixels[i].g * LumaWeights.y + pixels[i].b * LumaWeights.z;
            lMin = Mathf.Min(lMin, lum[i]);
            lMax = Mathf.Max(lMax, lum[i]);
            lMean += lum[i];
        }
        lMean = Mathf.Max(lMean / count, 1e-4f);
        float range = Mathf.Max(lMax - lMin, 1e-4f);

        var g = new float[count];
        for (int i = 0; i < count; i++)
            g[i] = (lum[i] - lMin) / range;
        var t = (float[])g.Clone();

        var fit = new Fit { colours = new Color[n], shade = g, position = t };
        var w = new float[3];
        const int samples = 128;
        var ramp = new Color[samples];
        var shadeAt = new float[samples];
        Color mean = Average(pixels);

        for (int it = 0; it < iterations; it++)
        {
            // Shade: brightness as a line over the shade value, relative to the mean brightness.
            float sg = 0f, sl = 0f, sgg = 0f, sgl = 0f;
            for (int i = 0; i < count; i++) { sg += g[i]; sl += lum[i]; sgg += g[i] * g[i]; sgl += g[i] * lum[i]; }
            float det = count * sgg - sg * sg;
            float slope = Mathf.Abs(det) > 1e-6f ? (count * sgl - sg * sl) / det : 0f;
            float intercept = (sl - slope * sg) / count;
            fit.shadeDark = Mathf.Max(intercept / lMean, 0f);
            fit.shadeLight = Mathf.Max((intercept + slope) / lMean, 0f);

            // Colours: least squares of pixel = sum_k w_k(position) * shade * C_k, with a slight
            // pull towards the mean colour so unused palette stops stay sensible.
            var ata = new double[n, n];
            var atb = new double[n, 3];
            for (int i = 0; i < count; i++)
            {
                RampWeights(t[i], n, w);
                float sh = Mathf.Lerp(fit.shadeDark, fit.shadeLight, g[i]);
                for (int a = 0; a < n; a++)
                {
                    double wa = w[a] * sh;
                    for (int b = 0; b < n; b++)
                        ata[a, b] += wa * w[b] * sh;
                    atb[a, 0] += wa * pixels[i].r;
                    atb[a, 1] += wa * pixels[i].g;
                    atb[a, 2] += wa * pixels[i].b;
                }
            }
            const double ridge = 1e-3;
            for (int a = 0; a < n; a++)
            {
                ata[a, a] += ridge;
                atb[a, 0] += ridge * mean.r;
                atb[a, 1] += ridge * mean.g;
                atb[a, 2] += ridge * mean.b;
            }
            var solved = Solve(ata, atb, n);
            for (int a = 0; a < n; a++)
                fit.colours[a] = new Color(Mathf.Max((float)solved[a, 0], 0f), Mathf.Max((float)solved[a, 1], 0f), Mathf.Max((float)solved[a, 2], 0f), 1f);

            if (it == iterations - 1)
                break;

            for (int c = 0; c < samples; c++)
            {
                float u = c / (samples - 1f);
                ramp[c] = Ramp(fit.colours, u, w);
                shadeAt[c] = Mathf.Lerp(fit.shadeDark, fit.shadeLight, u);
            }

            for (int i = 0; i < count; i++)
            {
                // Position for this texel's shade, then shade for that position.
                float sh = Mathf.Lerp(fit.shadeDark, fit.shadeLight, g[i]);
                float best = float.MaxValue;
                for (int c = 0; c < samples; c++)
                {
                    float d = Dist2(pixels[i], ramp[c] * sh);
                    if (d < best) { best = d; t[i] = c / (samples - 1f); }
                }
                var col = Ramp(fit.colours, t[i], w);
                best = float.MaxValue;
                for (int c = 0; c < samples; c++)
                {
                    float d = Dist2(pixels[i], col * shadeAt[c]);
                    if (d < best) { best = d; g[i] = c / (samples - 1f); }
                }
            }
        }

        // Keep the colours pickable (0..1): move any excess brightness into the shade.
        float maxChannel = 0f;
        foreach (var c in fit.colours)
            maxChannel = Mathf.Max(maxChannel, c.maxColorComponent);
        if (maxChannel > 1f)
        {
            for (int a = 0; a < n; a++)
            {
                var c = fit.colours[a] / maxChannel;
                c.a = 1f;
                fit.colours[a] = c;
            }
            fit.shadeDark *= maxChannel;
            fit.shadeLight *= maxChannel;
        }

        double err = 0;
        for (int i = 0; i < count; i++)
            err += Dist2(Ramp(fit.colours, t[i], w) * Mathf.Lerp(fit.shadeDark, fit.shadeLight, g[i]), pixels[i]);
        fit.error = (float)System.Math.Sqrt(err / (count * 3));
        return fit;
    }

    static double[,] Solve(double[,] a, double[,] b, int n)
    {
        // Gaussian elimination with partial pivoting, n <= 3.
        var m = (double[,])a.Clone();
        var r = (double[,])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (System.Math.Abs(m[row, col]) > System.Math.Abs(m[pivot, col])) pivot = row;
            for (int k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (int k = 0; k < 3; k++) (r[col, k], r[pivot, k]) = (r[pivot, k], r[col, k]);
            for (int row = 0; row < n; row++)
            {
                if (row == col || m[col, col] == 0) continue;
                double f = m[row, col] / m[col, col];
                for (int k = 0; k < n; k++) m[row, k] -= f * m[col, k];
                for (int k = 0; k < 3; k++) r[row, k] -= f * r[col, k];
            }
        }
        for (int row = 0; row < n; row++)
            for (int k = 0; k < 3; k++)
                r[row, k] = m[row, row] != 0 ? r[row, k] / m[row, row] : 0;
        return r;
    }

    static Color Average(Color[] pixels)
    {
        Color sum = Color.clear;
        foreach (var p in pixels) sum += p;
        return sum / Mathf.Max(1, pixels.Length);
    }

    /// <summary>Best fit with as few colours as match nearly as well as three.</summary>
    public static Fit FitBest(Color[] pixels)
    {
        var fits = new[] { FitPalette(pixels, 1), FitPalette(pixels, 2), FitPalette(pixels, 3) };
        float best = Mathf.Min(fits[0].error, fits[1].error, fits[2].error);
        foreach (var f in fits)
            if (f.error <= best + 0.003f)
                return f;
        return fits[2];
    }

    /// <summary>
    /// Converts a full colour texture into a new surface that renders as close to it as the
    /// palette allows. The relief keeps the texture's brightness (as before conversion).
    /// </summary>
    public static BlockSurface ConvertTexture(Texture2D texture, string name, int size, byte legacyMaskSlice, out float error)
    {
        var pixels = BlockTextureArrayBuilder.ReadPixels(texture, size);
        var fit = FitBest(pixels);
        error = fit.error;

        var surface = CreateAsset(name, out _);
        surface.colours = fit.colours;
        surface.shadeDark = fit.shadeDark;
        surface.shadeLight = fit.shadeLight;
        surface.patchContrast = 1f;
        surface.patchOffset = 0f;
        surface.texelColourVariation = 1f;
        var grey = new Color[size * size];
        for (int i = 0; i < grey.Length; i++)
            grey[i] = new Color(fit.shade[i], fit.position[i], fit.shade[i], 1f);
        surface.greyTexture = WritePng(OwnedPath(surface, Kind.Grey), grey, size);

        var heightBytes = new byte[size * size];
        BlockTextureArrayBuilder.BuildHeights(pixels, heightBytes);
        var heights = new float[heightBytes.Length];
        for (int i = 0; i < heights.Length; i++)
            heights[i] = heightBytes[i] / 255f;
        surface.reliefMask = WriteGreyPng(OwnedPath(surface, Kind.Relief), heights, size);

        if (legacyMaskSlice > 0)
            surface.alphaMask = LegacyMaskTexture(legacyMaskSlice);

        EditorUtility.SetDirty(surface);
        AssetDatabase.SaveAssets();
        return surface;
    }

    /// <summary>A legacy Resources/VoxelAlphaMasks slice as a shared PNG (exported once).</summary>
    public static Texture2D LegacyMaskTexture(int slice)
    {
        string path = $"{SharedMaskFolder}/LegacyMask{slice}.png";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null)
            return existing;

        var legacy = BlockDatabase.LegacyAlphaMasks;
        if (legacy == null || slice >= legacy.depth || !legacy.isReadable)
        {
            Debug.LogWarning($"[BlockSurfaceAssets] Legacy alpha mask slice {slice} is not available.");
            return null;
        }
        EnsureFolder(SharedMaskFolder);
        return WritePng(path, legacy.GetPixels(slice), legacy.width);
    }

    /// <summary>
    /// Batch mode: -executeMethod BlockSurfaceAssets.MigrateFromCommandLine. With -reconvert,
    /// blocks that still have their colour texture are converted again from scratch (their
    /// surfaces under Root are deleted first, painted edits included).
    /// </summary>
    public static void MigrateFromCommandLine()
    {
        bool reconvert = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-reconvert") >= 0;
        foreach (var guid in AssetDatabase.FindAssets("t:BlockTextureArrayBuilder"))
        {
            var builder = AssetDatabase.LoadAssetAtPath<BlockTextureArrayBuilder>(AssetDatabase.GUIDToAssetPath(guid));
            if (builder == null || builder.blockDatabase == null)
                continue;
            var db = builder.blockDatabase;
            if (reconvert)
            {
                for (int i = 0; i < db.blocks.Length; i++)
                {
                    var surface = db.blocks[i].surface;
                    if (surface == null || db.blocks[i].texture == null || !AssetDatabase.GetAssetPath(surface).StartsWith(Root + "/"))
                        continue;
                    AssetDatabase.DeleteAsset(FolderOf(surface));
                    db.blocks[i].surface = null;
                }
                EditorUtility.SetDirty(db);
                AssetDatabase.SaveAssets();
            }
            MigrateAll(db, builder.textureSize);
            builder.GenerateTextureArray();
        }
    }

    /// <summary>Gives every block with a legacy colour texture (and no surface) a converted surface.</summary>
    public static void MigrateAll(BlockDatabase db, int size, bool skipTranslucent = true)
    {
        Undo.RecordObject(db, "Migrate Block Textures");
        var report = new List<string>();
        for (int i = 0; i < db.blocks.Length; i++)
        {
            var entry = db.blocks[i];
            if (entry.surface != null || entry.texture == null || entry.type == BlockType.Air)
                continue;
            if (skipTranslucent && entry.visibility == BlockVisibility.Translucent)
            {
                report.Add($"{entry.type}: skipped (translucent, keeps its colour texture)");
                continue;
            }

            EditorUtility.DisplayProgressBar("Migrating block textures", entry.type.ToString(), (float)i / db.blocks.Length);
            byte mask = entry.visibility == BlockVisibility.Stacked ? entry.alphaMaskIndex : (byte)0;
            entry.surface = ConvertTexture(entry.texture, entry.type.ToString(), size, mask, out float error);
            db.blocks[i] = entry;
            report.Add($"{entry.type}: {entry.surface.ColourCount} colour(s), error {error * 255f:0.0}/255 RMS ({entry.texture.name})");
        }
        EditorUtility.ClearProgressBar();
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        Debug.Log("[Block Studio] Migration:\n" + string.Join("\n", report));
    }
}
#endif
