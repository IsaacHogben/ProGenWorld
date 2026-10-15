using System;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Top down map of the region generator: biome palette colours, hill shading, water and (optional)
/// where 3D shapes apply. Reads the scene's BiomeDataManager and ChunkManager seed / settings.
/// Batch mode: -executeMethod RegionMapWindow.BatchRender -regionMapOut map.png
/// [-regionMapExtent blocks] [-regionMapRes pixels] [-regionMapCenterX x] [-regionMapCenterZ z]
/// </summary>
public class RegionMapWindow : EditorWindow
{
    RegionGenSettingsSO settings;
    BiomeDataManager biomeDataManager;
    int seed = 1337;
    Vector2 center;
    float extent = 100000f;
    int resolution = 512;
    bool showDetail = true;

    Texture2D map;
    string stats = "";
    Vector2 legendScroll;

    [MenuItem("Window/ProGenWorld/Region Map")]
    static void Open() => GetWindow<RegionMapWindow>("Region Map");

    void OnEnable() => FindSceneDefaults();

    void FindSceneDefaults()
    {
        var chunkManager = FindFirstObjectByType<ChunkManager>();
        if (chunkManager != null)
        {
            seed = chunkManager.seed;
            if (settings == null)
                settings = new SerializedObject(chunkManager).FindProperty("regionGenSettings")?.objectReferenceValue as RegionGenSettingsSO;
            if (biomeDataManager == null)
                biomeDataManager = chunkManager.biomeDataManager;
        }
        if (biomeDataManager == null)
            biomeDataManager = FindFirstObjectByType<BiomeDataManager>();
    }

    void OnDisable()
    {
        if (map != null)
            DestroyImmediate(map);
    }

    void OnGUI()
    {
        settings = (RegionGenSettingsSO)EditorGUILayout.ObjectField("Settings", settings, typeof(RegionGenSettingsSO), false);
        biomeDataManager = (BiomeDataManager)EditorGUILayout.ObjectField("Biome Data", biomeDataManager, typeof(BiomeDataManager), true);
        seed = EditorGUILayout.IntField("Seed", seed);
        center = EditorGUILayout.Vector2Field("Centre (x, z)", center);
        extent = Mathf.Max(64f, EditorGUILayout.FloatField("Width (blocks)", extent));
        resolution = Mathf.Clamp(EditorGUILayout.IntField("Pixels", resolution), 32, 2048);
        showDetail = EditorGUILayout.Toggle("Show 3D Shape Weight", showDetail);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (settings == null && GUILayout.Button("Create Settings Asset"))
                settings = CreateSettingsAsset();
            GUI.enabled = settings != null && biomeDataManager != null;
            if (GUILayout.Button("Generate"))
                Generate();
            GUI.enabled = true;
            if (GUILayout.Button("Scene Defaults", GUILayout.Width(110)))
                FindSceneDefaults();
        }
        if (!string.IsNullOrEmpty(stats))
            EditorGUILayout.HelpBox(stats, MessageType.None);

        if (map == null)
            return;
        float size = Mathf.Min(position.width - 8f, position.height - 260f);
        var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
        GUI.DrawTexture(rect, map, ScaleMode.ScaleToFit, false);

        if (biomeDataManager != null && biomeDataManager.biomes != null)
        {
            legendScroll = EditorGUILayout.BeginScrollView(legendScroll, GUILayout.Height(90));
            foreach (var biome in biomeDataManager.biomes)
            {
                if (biome == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var swatch = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                    EditorGUI.DrawRect(swatch, BiomeColour(biome.BiomeID));
                    EditorGUILayout.LabelField(biome.biomeType.ToString());
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    void Generate()
    {
        if (map != null)
            DestroyImmediate(map);
        map = Render(settings, biomeDataManager, seed, center, extent, resolution, showDetail, out stats);
    }

    static RegionGenSettingsSO CreateSettingsAsset()
    {
        const string path = "Assets/ScriptableObjects/RegionGenSettings.asset";
        var existing = AssetDatabase.LoadAssetAtPath<RegionGenSettingsSO>(path);
        if (existing != null)
            return existing;
        var asset = CreateInstance<RegionGenSettingsSO>();
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        return asset;
    }

    public static Color BiomeColour(int id) => Color.HSVToRGB(id * 0.618034f % 1f, 0.55f, 0.95f);

    /// <summary>Renders the map with a temporary copy of the BiomeDataManager (edit mode safe).</summary>
    public static Texture2D Render(RegionGenSettingsSO settings, BiomeDataManager source, int seed,
        Vector2 center, float extent, int resolution, bool showDetail, out string stats)
    {
        var go = new GameObject("Region Map Biome Data") { hideFlags = HideFlags.HideAndDontSave };
        var data = go.AddComponent<BiomeDataManager>();
        EditorUtility.CopySerialized(source, data);
        RegionSystem regions = null;
        var columns = new NativeArray<ColumnSample>(resolution * resolution, Allocator.Persistent);
        var hints = new NativeArray<BiomeHint>(resolution * resolution, Allocator.Persistent);
        try
        {
            data.Initialize();
            regions = new RegionSystem(settings, data, seed, 64);

            float spacing = extent / resolution;
            float2 origin = new float2(center.x, center.y) - extent * 0.5f + spacing * 0.5f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            regions.ScheduleColumns(origin, spacing, resolution, columns, hints).Complete();
            double ms = watch.Elapsed.TotalMilliseconds;

            int water = data.GetWaterLevel();
            var pixels = new Color32[resolution * resolution];
            float minH = float.MaxValue, maxH = float.MinValue;
            int detailCount = 0, waterCount = 0;
            var biomeCount = new int[256];
            float3 light = math.normalize(new float3(-1f, 1.2f, -1f));

            for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                int i = x + z * resolution;
                var c = columns[i];
                minH = math.min(minH, c.height);
                maxH = math.max(maxH, c.height);
                biomeCount[c.palette]++;

                // Hill shading from neighbouring heights
                float hx = columns[math.min(x + 1, resolution - 1) + z * resolution].height - columns[math.max(x - 1, 0) + z * resolution].height;
                float hz = columns[x + math.min(z + 1, resolution - 1) * resolution].height - columns[x + math.max(z - 1, 0) * resolution].height;
                float3 n = math.normalize(new float3(-hx, 2f * spacing, -hz));
                float shade = math.saturate(0.55f + 0.6f * math.dot(n, light));

                Color col = BiomeColour(c.palette) * shade;
                if (c.height <= water)
                {
                    float depth = math.saturate((water - c.height) / 40f);
                    col = Color.Lerp(col, new Color(0.1f, 0.25f, 0.55f), 0.55f + 0.35f * depth);
                    waterCount++;
                }
                if (c.HasDetail)
                {
                    detailCount++;
                    if (showDetail)
                        col = Color.Lerp(col, new Color(0.9f, 0.1f, 0.1f), 0.5f * math.min(1f, c.DetailWeight));
                }
                col.a = 1f;
                pixels[i] = col;
            }

            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels32(pixels);
            tex.Apply();

            var sb = new System.Text.StringBuilder();
            sb.Append($"{resolution}x{resolution} columns in {ms:F0} ms, {spacing:F1} blocks per pixel. Height {minH:F0} .. {maxH:F0}, water {100f * waterCount / pixels.Length:F0}%, 3D shapes {100f * detailCount / pixels.Length:F0}%.\n");
            foreach (var biome in data.biomes)
                if (biome != null && biomeCount[biome.BiomeID] > 0)
                    sb.Append($"{biome.biomeType} {100f * biomeCount[biome.BiomeID] / pixels.Length:F0}%  ");
            stats = sb.ToString();
            return tex;
        }
        finally
        {
            regions?.Dispose();
            columns.Dispose();
            hints.Dispose();
            data.Dispose();
            DestroyImmediate(go);
        }
    }

    // ------------------------------------------------------------------------------------
    // Batch mode
    // ------------------------------------------------------------------------------------

    public static void BatchRender()
    {
        string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        string outPath = Arg("-regionMapOut", "RegionMap.png");
        float extent = float.Parse(Arg("-regionMapExtent", "100000"), System.Globalization.CultureInfo.InvariantCulture);
        int res = int.Parse(Arg("-regionMapRes", "512"));
        float cx = float.Parse(Arg("-regionMapCenterX", "0"), System.Globalization.CultureInfo.InvariantCulture);
        float cz = float.Parse(Arg("-regionMapCenterZ", "0"), System.Globalization.CultureInfo.InvariantCulture);

        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Scene.unity");
        var w = CreateInstance<RegionMapWindow>();
        w.FindSceneDefaults();
        if (w.settings == null)
            w.settings = CreateSettingsAsset();

        var tex = Render(w.settings, w.biomeDataManager, w.seed, new Vector2(cx, cz), extent, res, true, out var stats);
        File.WriteAllBytes(outPath, tex.EncodeToPNG());
        Debug.Log($"[RegionMap] Wrote {outPath}\n{stats}");
        DestroyImmediate(tex);

        DensitySmokeTest(w.settings, w.biomeDataManager, w.seed, new Vector2(cx, cz));
        DestroyImmediate(w);
    }

    // Fills the density of the chunks a column's surface passes through and logs what came out
    static void DensitySmokeTest(RegionGenSettingsSO settings, BiomeDataManager source, int seed, Vector2 at)
    {
        var chunkManager = FindFirstObjectByType<ChunkManager>();
        int chunkSize = chunkManager != null ? chunkManager.chunkSize : 64;
        float frequency = chunkManager != null ? chunkManager.frequency : 1f;

        var go = new GameObject("Region Smoke Biome Data") { hideFlags = HideFlags.HideAndDontSave };
        var data = go.AddComponent<BiomeDataManager>();
        EditorUtility.CopySerialized(source, data);
        int side = chunkSize + 1;
        var columns = new NativeArray<ColumnSample>(side * side, Allocator.Persistent);
        var hints = new NativeArray<BiomeHint>(side * side, Allocator.Persistent);
        var density = new NativeArray<float>(side * side * side, Allocator.Persistent);
        RegionSystem regions = null;

        // Exercise the 3D shape path too: with no biome using one, borrow the first terrain tree
        // for every biome for the duration of the test
        var borrowed = new System.Collections.Generic.List<BiomeDefinitionSO>();
        bool anyDetail = false;
        foreach (var b in source.biomes)
            if (b != null && b.regionShape?.detailTerrain != null) anyDetail = true;
        if (!anyDetail && source.terrainTypes != null && source.terrainTypes.Length > 0)
            foreach (var b in source.biomes)
                if (b != null && b.regionShape != null)
                {
                    b.regionShape.detailTerrain = source.terrainTypes[0];
                    borrowed.Add(b);
                }
        try
        {
            data.Initialize();
            regions = new RegionSystem(settings, data, seed, chunkSize);
            var hybrid = new HybridDensity(data, settings, seed, frequency);

            int2 column = (int2)math.floor(new float2(at.x, at.y) / chunkSize);
            regions.ScheduleColumns((float2)(column * chunkSize), 1f, side, columns, hints).Complete();

            float lo = float.MaxValue, hi = float.MinValue;
            int detailColumns = 0;
            foreach (var c in columns)
            {
                lo = math.min(lo, math.min(c.height, c.bandMin));
                hi = math.max(hi, c.bandMax);
                if (c.HasDetail) detailColumns++;
            }

            var sb = new System.Text.StringBuilder($"[RegionMap] Column {column}: band {lo:F1} .. {hi:F1}, {detailColumns} columns with 3D shapes\n");
            for (int cy = (int)math.floor((lo - 1f) / chunkSize); cy <= (int)math.floor(hi / chunkSize); cy++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                hybrid.Fill(density, new int3(column.x, cy, column.y), chunkSize, 1, columns);
                double ms = watch.Elapsed.TotalMilliseconds;
                int solid = 0, nan = 0;
                float dmin = float.MaxValue, dmax = float.MinValue;
                foreach (float d in density)
                {
                    if (float.IsNaN(d)) { nan++; continue; }
                    if (d <= 0f) solid++;
                    dmin = math.min(dmin, d);
                    dmax = math.max(dmax, d);
                }
                sb.Append($"  chunk y {cy}: {100f * solid / density.Length:F1}% solid, density {dmin:F3} .. {dmax:F3}, NaN {nan}, {ms:F1} ms\n");
            }
            Debug.Log(sb.ToString());
        }
        finally
        {
            regions?.Dispose();
            columns.Dispose();
            hints.Dispose();
            density.Dispose();
            data.Dispose();
            DestroyImmediate(go);
            foreach (var b in borrowed)
                b.regionShape.detailTerrain = null;
        }
    }
}
