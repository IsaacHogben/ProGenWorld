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
/// [-regionMapOverlay None|Shapes3D|Decoration|SubBiomes] [-regionMapOnlyBiome BiomeType]
/// </summary>
public class RegionMapWindow : EditorWindow
{
    public enum Overlay { None, Shapes3D, Decoration, SubBiomes }

    readonly RegionMapView view = new RegionMapView();
    Vector2 legendScroll;

    // Kept for batch mode (it reads these off a window instance)
    RegionGenSettingsSO settings { get => view.settings; set => view.settings = value; }
    BiomeDataManager biomeDataManager { get => view.biomeDataManager; set => view.biomeDataManager = value; }
    int seed { get => view.seed; set => view.seed = value; }

    [MenuItem("Window/ProGenWorld/Region Map")]
    static void Open() => GetWindow<RegionMapWindow>("Region Map");

    void OnEnable()
    {
        FindSceneDefaults();
        wantsMouseMove = true;
        EditorApplication.update += Tick;
    }

    void OnDisable()
    {
        EditorApplication.update -= Tick;
        view.Dispose();
    }

    void Tick()
    {
        if (view.Tick())
            Repaint();
    }

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

    void OnGUI()
    {
        EditorGUI.BeginChangeCheck();
        settings = (RegionGenSettingsSO)EditorGUILayout.ObjectField("Settings", settings, typeof(RegionGenSettingsSO), false);
        biomeDataManager = (BiomeDataManager)EditorGUILayout.ObjectField("Biome Data", biomeDataManager, typeof(BiomeDataManager), true);
        seed = EditorGUILayout.IntField("Seed", seed);
        view.center = EditorGUILayout.Vector2Field("Centre (x, z)", view.center);
        view.extent = Mathf.Clamp(EditorGUILayout.FloatField("Width (blocks)", view.extent), RegionMapView.MinExtent, RegionMapView.MaxExtent);
        view.resolution = Mathf.Clamp(EditorGUILayout.IntField("Pixels", view.resolution), 32, 2048);
        view.overlay = (Overlay)EditorGUILayout.EnumPopup("Overlay", view.overlay);
        if (EditorGUI.EndChangeCheck())
            view.Changed();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (settings == null && GUILayout.Button("Create Settings Asset"))
                settings = CreateSettingsAsset();
            GUI.enabled = view.CanRender;
            if (GUILayout.Button("Generate"))
                view.Generate();
            GUI.enabled = true;
            if (GUILayout.Button("Reset View", GUILayout.Width(90)))
                view.ResetView();
            if (GUILayout.Button("Scene Defaults", GUILayout.Width(110)))
                FindSceneDefaults();
            view.autoUpdate = GUILayout.Toggle(view.autoUpdate, "Auto Update", GUILayout.Width(95));
        }
        if (!string.IsNullOrEmpty(view.Stats))
            EditorGUILayout.HelpBox(view.Stats, MessageType.None);

        if (biomeDataManager != null && biomeDataManager.biomes != null)
        {
            legendScroll = EditorGUILayout.BeginScrollView(legendScroll, GUILayout.Height(40));
            using (new EditorGUILayout.HorizontalScope())
                foreach (var biome in biomeDataManager.biomes)
                {
                    if (biome == null) continue;
                    var swatch = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                    EditorGUI.DrawRect(swatch, BiomeColour(biome.BiomeID));
                    GUILayout.Label(biome.biomeType.ToString(), GUILayout.ExpandWidth(false));
                    GUILayout.Space(6);
                }
            EditorGUILayout.EndScrollView();
        }

        EditorGUILayout.LabelField(view.Readout(), EditorStyles.miniLabel);
        var area = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        float size = Mathf.Max(10f, Mathf.Min(area.width, area.height));
        view.OnGUI(new Rect(area.x + (area.width - size) * 0.5f, area.y, size, size), this);
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

    /// <summary>Colour of a biome's n-th sub-biome layer in the Sub-biomes overlay.</summary>
    public static Color LayerColour(int n) => Color.HSVToRGB((0.08f + n * 0.29f) % 1f, 0.8f, 1f);

    /// <summary>A rendered map: its texture and, per pixel (x fastest, +z up), what is there.</summary>
    public sealed class MapResult : IDisposable
    {
        public Texture2D texture;
        public string stats = "";
        public int resolution;
        public byte[] palette;       // palette biome
        public float[] heights;      // surface height
        public short[] layer;        // strongest sub-biome layer (index into layerNames), -1 = none
        public float[] layerMask;
        public string[] layerNames;  // "Biome / Layer", by RegionDetailSources.Layers index

        public void Dispose()
        {
            if (texture != null)
                DestroyImmediate(texture);
            texture = null;
        }
    }

    /// <summary>Renders the map with a temporary copy of the BiomeDataManager (edit mode safe).</summary>
    public static Texture2D Render(RegionGenSettingsSO settings, BiomeDataManager source, int seed,
        Vector2 center, float extent, int resolution, Overlay overlay, out string stats)
    {
        var r = RenderMap(settings, source, seed, center, extent, resolution, overlay);
        stats = r.stats;
        return r.texture;
    }

    /// <summary>Renders the map (onlyBiome >= 0: every climate cell holds just that biome).</summary>
    public static MapResult RenderMap(RegionGenSettingsSO settings, BiomeDataManager source, int seed,
        Vector2 center, float extent, int resolution, Overlay overlay, int onlyBiome = -1)
    {
        var result = new MapResult { resolution = resolution };
        var go = new GameObject("Region Map Biome Data") { hideFlags = HideFlags.HideAndDontSave };
        var data = go.AddComponent<BiomeDataManager>();
        EditorUtility.CopySerialized(source, data);
        RegionSystem regions = null;
        var columns = new NativeArray<ColumnSample>(resolution * resolution, Allocator.Persistent);
        var hints = new NativeArray<BiomeHint>(resolution * resolution, Allocator.Persistent);
        try
        {
            data.Initialize();
            if (onlyBiome >= 0)
            {
                var cells = data.GetClimateCellBiomes();
                for (int i = 0; i < cells.Length; i++)
                    cells[i] = (byte)onlyBiome;
            }
            var cm = FindFirstObjectByType<ChunkManager>();
            regions = new RegionSystem(settings, data, seed, 64, cm != null ? cm.frequency : 1f);

            float spacing = extent / resolution;
            float2 origin = new float2(center.x, center.y) - extent * 0.5f + spacing * 0.5f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            regions.CompleteArea(origin, spacing, resolution);
            double riverMs = watch.Elapsed.TotalMilliseconds;
            regions.TryScheduleColumns(origin, spacing, resolution, columns, hints, out var handle);
            handle.Complete();
            double ms = watch.Elapsed.TotalMilliseconds - riverMs;

            var pixels = new Color32[resolution * resolution];
            var palette = result.palette = new byte[resolution * resolution];
            var heights = result.heights = new float[resolution * resolution];
            var layer = result.layer = new short[resolution * resolution];
            var layerMask = result.layerMask = new float[resolution * resolution];
            var names = new System.Collections.Generic.List<string>();
            foreach (var (b, l, _) in RegionDetailSources.Layers(data))
                names.Add($"{b.biomeType} / {l.name}");
            result.layerNames = names.ToArray();
            var shapes = regions.Shapes;
            var subs = regions.SubBiomes;
            float minH = float.MaxValue, maxH = float.MinValue;
            int detailCount = 0, waterCount = 0;
            var biomeCount = new int[256];
            float3 light = math.normalize(new float3(-1f, 1.2f, -1f));

            for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                int i = x + z * resolution;
                var c = columns[i];
                palette[i] = c.palette;
                heights[i] = c.height;
                minH = math.min(minH, c.height);
                maxH = math.max(maxH, c.height);
                biomeCount[c.palette]++;

                // Hill shading from neighbouring heights
                float hx = columns[math.min(x + 1, resolution - 1) + z * resolution].height - columns[math.max(x - 1, 0) + z * resolution].height;
                float hz = columns[x + math.min(z + 1, resolution - 1) * resolution].height - columns[x + math.max(z - 1, 0) * resolution].height;
                float3 n = math.normalize(new float3(-hx, 2f * spacing, -hz));
                float shade = math.saturate(0.55f + 0.6f * math.dot(n, light));

                Color col = BiomeColour(c.palette) * shade;
                if (c.height < c.waterY)
                {
                    float depth = math.saturate((c.waterY - c.height) / 40f);
                    col = Color.Lerp(col, new Color(0.1f, 0.25f, 0.55f), 0.55f + 0.35f * depth);
                    waterCount++;
                }
                if (c.HasDetail)
                {
                    detailCount++;
                    if (overlay == Overlay.Shapes3D)
                        col = Color.Lerp(col, new Color(0.9f, 0.1f, 0.1f), 0.5f * math.min(1f, c.DetailWeight));
                }
                // Strongest sub-biome layer of the palette biome here
                layer[i] = -1;
                var shape = shapes[c.palette];
                float wx = origin.x + x * spacing, wz = origin.y + z * spacing;
                for (int k = shape.subStart; k < shape.subStart + shape.subCount; k++)
                {
                    float m = RegionMath.SubMask(subs[k], wx, wz);
                    if (m > layerMask[i])
                    {
                        layerMask[i] = m;
                        layer[i] = (short)k;
                    }
                }
                if (overlay == Overlay.SubBiomes && layer[i] >= 0)
                    col = Color.Lerp(col, LayerColour(layer[i] - shape.subStart) * shade, 0.8f * layerMask[i]);
                if (overlay == Overlay.Decoration)
                {
                    // Grey = normal, green = thicker, red = sparser
                    float d = c.decoScale;
                    var tint = d >= 1f ? Color.Lerp(Color.grey, Color.green, math.saturate((d - 1f) / 2f)) : Color.Lerp(new Color(0.8f, 0.1f, 0.1f), Color.grey, d);
                    col = Color.Lerp(col, tint * shade, 0.75f);
                }
                col.a = 1f;
                pixels[i] = col;
            }

            // Rivers are far narrower than a pixel at map scale: draw them as lines
            int riverEdges = 0, lakes = 0;
            float2 min = origin - spacing * 0.5f, max = min + extent;
            using (var q = regions.Rivers.Query(min, max, Allocator.Temp))
            {
                riverEdges = q.edges.Length;
                lakes = q.lakes.Length;
                var river = new Color32(40, 90, 200, 255);
                foreach (var e in q.edges)
                {
                    float2 a = (e.a - min) / spacing, b = (e.b - min) / spacing;
                    int steps = (int)math.ceil(math.distance(a, b)) + 1;
                    int thick = math.max(0, (int)(e.halfWidth / spacing));
                    for (int k = 0; k <= steps; k++)
                    {
                        int2 p = (int2)math.lerp(a, b, k / (float)steps);
                        for (int dz = -thick; dz <= thick; dz++)
                        for (int dx = -thick; dx <= thick; dx++)
                        {
                            int px = p.x + dx, pz = p.y + dz;
                            if (px >= 0 && pz >= 0 && px < resolution && pz < resolution)
                                pixels[px + pz * resolution] = river;
                        }
                    }
                }
            }

            var tex = result.texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels32(pixels);
            tex.Apply();

            var sb = new System.Text.StringBuilder();
            sb.Append($"{resolution}x{resolution} columns in {ms:F0} ms (rivers planned in {riverMs:F0} ms), {spacing:F1} blocks per pixel. Height {minH:F0} .. {maxH:F0}, water {100f * waterCount / pixels.Length:F0}%, 3D shapes {100f * detailCount / pixels.Length:F0}%, {riverEdges / 6} river reaches, {lakes} lakes.\n");
            foreach (var biome in data.biomes)
                if (biome != null && biomeCount[biome.BiomeID] > 0)
                    sb.Append($"{biome.biomeType} {100f * biomeCount[biome.BiomeID] / pixels.Length:F0}%  ");
            result.stats = sb.ToString();
            return result;
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

        var overlay = Enum.TryParse(Arg("-regionMapOverlay", "Shapes3D"), true, out Overlay o) ? o : Overlay.Shapes3D;
        // -regionMapTestLayers 1: every biome without sub-biomes gets two sample layers for this render
        var borrowedLayers = new System.Collections.Generic.List<BiomeDefinitionSO>();
        if (Arg("-regionMapTestLayers", "0") == "1")
            foreach (var b in w.biomeDataManager.biomes)
                if (b != null && b.regionShape != null && (b.regionShape.subBiomes == null || b.regionShape.subBiomes.Length == 0))
                {
                    b.regionShape.subBiomes = new[]
                    {
                        new SubBiomeLayer { name = "Plains + thickets", kind = SubBiomeKind.Continuous, scale = 900f, coverage = 0.35f, softness = 0.3f, hillMultiplier = 0.1f, decorationMultiplier = 3f },
                        new SubBiomeLayer { name = "Gravel rises", kind = SubBiomeKind.Patches, scale = 500f, coverage = 0.3f, softness = 0.4f, heightOffset = 12f, decorationMultiplier = 0.2f, surfaceBlock = BlockType.Stone }
                    };
                    borrowedLayers.Add(b);
                }
        Texture2D tex;
        string stats;
        try
        {
            int only = Enum.TryParse(Arg("-regionMapOnlyBiome", ""), true, out BiomeType ob) ? (int)ob : -1;
            var r = RenderMap(w.settings, w.biomeDataManager, w.seed, new Vector2(cx, cz), extent, res, overlay, only);
            tex = r.texture;
            stats = r.stats;
        }
        finally
        {
            foreach (var b in borrowedLayers)
                b.regionShape.subBiomes = new SubBiomeLayer[0];
        }
        File.WriteAllBytes(outPath, tex.EncodeToPNG());
        Debug.Log($"[RegionMap] Wrote {outPath}\n{stats}");
        DestroyImmediate(tex);

        DensitySmokeTest(w.settings, w.biomeDataManager, w.seed, new Vector2(cx, cz));
        DestroyImmediate(w);
    }

    /// <summary>
    /// Times the region density fill on one thread: every chunk of a block of chunk columns
    /// (-densityBenchColumns n, default 6) at -regionMapCenterX/Z, optionally one biome only
    /// (-regionMapOnlyBiome) and a shape sample spacing for every tree (-densityBenchSpacing).
    /// </summary>
    public static void BenchDensity()
    {
        string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Scene.unity");
        var w = CreateInstance<RegionMapWindow>();
        w.FindSceneDefaults();
        var chunkManager = FindFirstObjectByType<ChunkManager>();
        int chunkSize = chunkManager != null ? chunkManager.chunkSize : 64;
        float frequency = chunkManager != null ? chunkManager.frequency : 1f;
        int columnsSide = int.Parse(Arg("-densityBenchColumns", "6"));
        int sampleRes = int.Parse(Arg("-densityBenchSampleRes", "1"));
        float cx = float.Parse(Arg("-regionMapCenterX", "0"), System.Globalization.CultureInfo.InvariantCulture);
        float cz = float.Parse(Arg("-regionMapCenterZ", "0"), System.Globalization.CultureInfo.InvariantCulture);
        int only = Enum.TryParse(Arg("-regionMapOnlyBiome", ""), true, out BiomeType ob) ? (int)ob : -1;
        string spacing = Arg("-densityBenchSpacing", "");

        var source = w.biomeDataManager;
        var changed = new System.Collections.Generic.Dictionary<TerrainTypeDefinitionSO, ShapeSampleSpacing>();
        if (spacing != "")
            foreach (var b in source.biomes)
            {
                if (b?.regionShape == null) continue;
                var terrains = new System.Collections.Generic.List<TerrainTypeDefinitionSO> { b.regionShape.detailTerrain };
                if (b.regionShape.subBiomes != null)
                    foreach (var l in b.regionShape.subBiomes) terrains.Add(l?.detailTerrain);
                foreach (var t in terrains)
                    if (t != null && !changed.ContainsKey(t))
                    {
                        changed[t] = t.shapeSampleSpacing;
                        t.shapeSampleSpacing = (ShapeSampleSpacing)int.Parse(spacing);
                    }
            }

        var go = new GameObject("Density Bench Biome Data") { hideFlags = HideFlags.HideAndDontSave };
        var data = go.AddComponent<BiomeDataManager>();
        EditorUtility.CopySerialized(source, data);
        int side = chunkSize + 1;
        var columns = new NativeArray<ColumnSample>(side * side, Allocator.Persistent);
        var hints = new NativeArray<BiomeHint>(side * side, Allocator.Persistent);
        int n = chunkSize / sampleRes + 1;
        var density = new NativeArray<float>(n * n * n, Allocator.Persistent);
        RegionSystem regions = null;
        try
        {
            data.Initialize();
            if (only >= 0)
            {
                var cells = data.GetClimateCellBiomes();
                for (int i = 0; i < cells.Length; i++) cells[i] = (byte)only;
            }
            regions = new RegionSystem(w.settings, data, w.seed, chunkSize, frequency);
            var hybrid = new HybridDensity(data, w.settings, w.seed, frequency);
            Array.Clear(HybridDensity.TreeMs, 0, 8);
            Array.Clear(HybridDensity.TreeSamples, 0, 8);

            int2 c0 = (int2)math.floor(new float2(cx, cz) / chunkSize) - columnsSide / 2;
            int chunks = 0;
            double fillMs = 0;
            for (int gz = 0; gz < columnsSide; gz++)
            for (int gx = 0; gx < columnsSide; gx++)
            {
                int2 column = c0 + new int2(gx, gz);
                regions.CompleteArea((float2)(column * chunkSize), 1f, side);
                regions.TryScheduleColumns((float2)(column * chunkSize), 1f, side, columns, hints, out var handle);
                handle.Complete();
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var c in columns)
                {
                    lo = math.min(lo, math.min(c.height, c.bandMin));
                    hi = math.max(hi, math.max(c.bandMax, c.height));
                }
                for (int cy = (int)math.floor((lo - 1f) / chunkSize); cy <= (int)math.floor(hi / chunkSize); cy++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    hybrid.Fill(density, new int3(column.x, cy, column.y), chunkSize, sampleRes, columns);
                    fillMs += watch.Elapsed.TotalMilliseconds;
                    chunks++;
                }
            }
            int level = math.tzcnt(sampleRes);
            double treeMs = HybridDensity.TreeMs[level];
            Debug.Log($"[DensityBench] {chunks} chunks (sampleRes {sampleRes}, spacing {(spacing == "" ? "as set" : spacing)}): fill {fillMs:F0} ms = {fillMs / chunks:F1} ms each; trees {treeMs:F0} ms for {HybridDensity.TreeSamples[level] / 1e6:F1}M samples; the rest {fillMs - treeMs:F0} ms");
        }
        finally
        {
            regions?.Dispose();
            columns.Dispose();
            hints.Dispose();
            density.Dispose();
            data.Dispose();
            DestroyImmediate(go);
            foreach (var kv in changed) kv.Key.shapeSampleSpacing = kv.Value;
            DestroyImmediate(w);
        }
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
            regions = new RegionSystem(settings, data, seed, chunkSize, frequency);
            var hybrid = new HybridDensity(data, settings, seed, frequency);

            int2 column = (int2)math.floor(new float2(at.x, at.y) / chunkSize);
            regions.CompleteArea((float2)(column * chunkSize), 1f, side);
            regions.TryScheduleColumns((float2)(column * chunkSize), 1f, side, columns, hints, out var handle);
            handle.Complete();

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
