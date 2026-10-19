using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Edits biomes in one place: the biome list on the left, the selected biome in tabs (palette,
/// shape, sub-biomes, decorations, streams) in the middle, and a live region map of it on the
/// right. Sub-biome layers are cards, each with its mask and effects, its own 3D shape and its
/// own decorations.
/// </summary>
public class BiomeEditorWindow : EditorWindow
{
    enum Tab { Palette, Shape, SubBiomes, Decorations, Streams }
    static readonly string[] TabNames = { "Palette", "Shape", "Sub-biomes", "Decorations", "Streams" };
    static readonly string[] CardSections = { "Mask & Effects", "3D Shape", "Decorations" };

    BiomeDefinitionSO selected;
    SerializedObject so;
    Tab tab = Tab.SubBiomes;
    Vector2 listScroll, editScroll;
    readonly List<BiomeDefinitionSO> biomes = new();
    readonly HashSet<BiomeDefinitionSO> inUse = new();
    readonly Dictionary<int, int> cardSection = new();

    readonly RegionMapView map = new RegionMapView
    {
        extent = 20000f,
        resolution = 256,
        overlay = RegionMapWindow.Overlay.SubBiomes
    };
    bool onlyThisBiome = true;
    bool showMap = true;

    const float ListWidth = 170f, MapWidth = 340f;

    [MenuItem("Window/ProGenWorld/Biome Editor")]
    static void Open() => GetWindow<BiomeEditorWindow>("Biome Editor");

    void OnEnable()
    {
        wantsMouseMove = true;
        FindSceneDefaults();
        RefreshList();
        if (selected == null && Selection.activeObject is BiomeDefinitionSO s)
            Select(s);
        else if (selected == null && biomes.Count > 0)
            Select(biomes[0]);
        EditorApplication.update += Tick;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    void OnDisable()
    {
        EditorApplication.update -= Tick;
        Undo.undoRedoPerformed -= OnUndoRedo;
        map.Dispose();
    }

    void Tick()
    {
        if (showMap && map.Tick())
            Repaint();
    }

    void OnUndoRedo()
    {
        so?.Update();
        map.Changed();
        Repaint();
    }

    void OnSelectionChange()
    {
        if (Selection.activeObject is BiomeDefinitionSO s && s != selected)
        {
            Select(s);
            Repaint();
        }
    }

    void FindSceneDefaults()
    {
        var chunkManager = FindFirstObjectByType<ChunkManager>();
        if (chunkManager != null)
        {
            map.seed = chunkManager.seed;
            map.settings = new SerializedObject(chunkManager).FindProperty("regionGenSettings")?.objectReferenceValue as RegionGenSettingsSO;
            map.biomeDataManager = chunkManager.biomeDataManager;
        }
        if (map.biomeDataManager == null)
            map.biomeDataManager = FindFirstObjectByType<BiomeDataManager>();
        if (map.settings == null)
            map.settings = AssetDatabase.FindAssets("t:RegionGenSettingsSO")
                .Select(g => AssetDatabase.LoadAssetAtPath<RegionGenSettingsSO>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault();
    }

    void RefreshList()
    {
        biomes.Clear();
        foreach (var guid in AssetDatabase.FindAssets("t:BiomeDefinitionSO"))
        {
            var b = AssetDatabase.LoadAssetAtPath<BiomeDefinitionSO>(AssetDatabase.GUIDToAssetPath(guid));
            if (b != null)
                biomes.Add(b);
        }
        biomes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        inUse.Clear();
        if (map.biomeDataManager?.biomes != null)
            foreach (var b in map.biomeDataManager.biomes)
                if (b != null)
                    inUse.Add(b);
    }

    void Select(BiomeDefinitionSO biome)
    {
        selected = biome;
        so = biome != null ? new SerializedObject(biome) : null;
        cardSection.Clear();
        UpdateMapBiome();
    }

    void UpdateMapBiome()
    {
        map.onlyBiome = onlyThisBiome && selected != null ? selected.BiomeID : -1;
        map.Changed();
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawList();
            DrawEditor();
            if (showMap)
                DrawMap();
        }
    }

    // ------------------------------------------------------------------------------------
    // Biome list
    // ------------------------------------------------------------------------------------

    void DrawList()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(ListWidth)))
        {
            listScroll = EditorGUILayout.BeginScrollView(listScroll);
            foreach (var b in biomes)
            {
                var rect = GUILayoutUtility.GetRect(ListWidth - 20f, 20f);
                bool isSelected = b == selected;
                if (Event.current.type == EventType.Repaint && isSelected)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.37f, 0.59f));
                EditorGUI.DrawRect(new Rect(rect.x + 3, rect.y + 4, 12, 12), RegionMapWindow.BiomeColour(b.BiomeID));
                var style = inUse.Count == 0 || inUse.Contains(b) ? EditorStyles.label : EditorStyles.centeredGreyMiniLabel;
                GUI.Label(new Rect(rect.x + 20, rect.y + 1, rect.width - 20, rect.height), new GUIContent(b.name,
                    inUse.Count == 0 || inUse.Contains(b) ? b.biomeType.ToString() : "Not in BiomeDataManager's biome list"), style);
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    Select(b);
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Refresh"))
            {
                FindSceneDefaults();
                RefreshList();
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Biome editor
    // ------------------------------------------------------------------------------------

    void DrawEditor()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            if (selected == null || so == null || so.targetObject == null)
            {
                EditorGUILayout.HelpBox("Select a biome.", MessageType.Info);
                return;
            }
            so.Update();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"{selected.name}  ({selected.biomeType})", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
                    EditorGUIUtility.PingObject(selected);
                showMap = GUILayout.Toggle(showMap, "Map", EditorStyles.miniButton, GUILayout.Width(40));
            }
            tab = (Tab)GUILayout.Toolbar((int)tab, TabNames);

            editScroll = EditorGUILayout.BeginScrollView(editScroll);
            EditorGUI.BeginChangeCheck();
            switch (tab)
            {
                case Tab.Palette: DrawPalette(); break;
                case Tab.Shape: DrawShape(); break;
                case Tab.SubBiomes: DrawSubBiomes(); break;
                case Tab.Decorations: DrawDecorations(); break;
                case Tab.Streams: DrawStreams(); break;
            }
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndScrollView();

            if (so.ApplyModifiedProperties() || changed)
                if (tab != Tab.Decorations && tab != Tab.Streams)
                    map.Changed();
        }
    }

    void Field(SerializedProperty parent, string name)
    {
        var p = parent != null ? parent.FindPropertyRelative(name) : so.FindProperty(name);
        if (p != null)
            EditorGUILayout.PropertyField(p, true);
    }

    void DrawPalette()
    {
        foreach (var name in new[] { "biomeType", "rarityWeight", "surfaceBlock", "subsurfaceBlock", "deepBlock",
                     "sedimentBlock", "gradientThreshold1", "gradientThreshold2" })
            Field(null, name);
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Climate ranges are only used when no ClimateGrid is assigned.", MessageType.None);
        Field(null, "humidityRange");
        Field(null, "temperatureRange");
    }

    void DrawShape()
    {
        var shape = so.FindProperty("regionShape");
        var it = shape.Copy();
        var end = shape.GetEndProperty();
        if (!it.NextVisible(true))
            return;
        var overrides = new Dictionary<string, string>
        {
            { "overrideSurfaceOffset", "detailSurfaceOffset" },
            { "overrideRangeUp", "detailRangeUp" },
            { "overrideRangeDown", "detailRangeDown" }
        };
        while (!SerializedProperty.EqualContents(it, end))
        {
            if (overrides.TryGetValue(it.name, out var valueName))
                OverrideRow(shape, it.name, valueName);
            else if (it.name != "subBiomes" && !overrides.ContainsValue(it.name))
                EditorGUILayout.PropertyField(it, true);
            if (!it.NextVisible(false))
                break;
        }
        EditorGUILayout.HelpBox("Sub-biome layers are on the Sub-biomes tab.", MessageType.None);
    }

    void DrawDecorations()
    {
        EditorGUILayout.HelpBox("The biome's own decorations. Sub-biome layers add to or replace these where they are (see each layer's card); Decoration Multiplier on a layer scales these.", MessageType.None);
        foreach (var name in new[] { "LSystemDecorations", "trees", "vegetation", "rocks", "aliens" })
            Field(null, name);
    }

    void DrawStreams()
    {
        var streams = so.FindProperty("streams");
        streams.isExpanded = true;
        EditorGUILayout.PropertyField(streams, true);
    }

    // ------------------------------------------------------------------------------------
    // Sub-biome cards
    // ------------------------------------------------------------------------------------

    void DrawSubBiomes()
    {
        var layers = so.FindProperty("regionShape.subBiomes");
        int remove = -1, moveUp = -1, moveDown = -1, duplicate = -1;
        for (int i = 0; i < layers.arraySize; i++)
        {
            var layer = layers.GetArrayElementAtIndex(i);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var swatch = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(16));
                    EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + 3, 12, 12), RegionMapWindow.LayerColour(i));
                    layer.isExpanded = EditorGUILayout.Foldout(layer.isExpanded, layer.FindPropertyRelative("name").stringValue, true, EditorStyles.foldoutHeader);
                    GUILayout.Label(Summary(layer), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    GUI.enabled = i > 0;
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22))) moveUp = i;
                    GUI.enabled = i < layers.arraySize - 1;
                    if (GUILayout.Button("▼", EditorStyles.miniButtonMid, GUILayout.Width(22))) moveDown = i;
                    GUI.enabled = true;
                    if (GUILayout.Button(new GUIContent("⧉", "Duplicate"), EditorStyles.miniButtonMid, GUILayout.Width(22))) duplicate = i;
                    if (GUILayout.Button(new GUIContent("✕", "Remove"), EditorStyles.miniButtonRight, GUILayout.Width(22))) remove = i;
                }
                if (!layer.isExpanded)
                    continue;

                cardSection.TryGetValue(i, out int section);
                section = GUILayout.Toolbar(section, CardSections, EditorStyles.miniButton);
                cardSection[i] = section;
                EditorGUILayout.Space(2);
                switch (section)
                {
                    case 0: DrawMaskAndEffects(layer); break;
                    case 1: DrawLayerShape(layer); break;
                    case 2: DrawLayerDecorations(layer); break;
                }
            }
        }

        if (moveUp > 0) layers.MoveArrayElement(moveUp, moveUp - 1);
        else if (moveDown >= 0) layers.MoveArrayElement(moveDown, moveDown + 1);
        else if (duplicate >= 0)
        {
            layers.InsertArrayElementAtIndex(duplicate);
            var copy = layers.GetArrayElementAtIndex(duplicate + 1);
            copy.FindPropertyRelative("name").stringValue += " copy";
        }
        else if (remove >= 0) layers.DeleteArrayElementAtIndex(remove);

        if (GUILayout.Button("Add Sub-biome"))
        {
            int n = layers.arraySize;
            layers.InsertArrayElementAtIndex(n);
            var layer = layers.GetArrayElementAtIndex(n);
            if (n == 0)
                ResetLayer(layer);   // a fresh element starts all zero
            layer.FindPropertyRelative("name").stringValue = $"Layer {n + 1}";
            layer.isExpanded = true;
        }
    }

    // A new layer's defaults (as SubBiomeLayer's field initialisers)
    static void ResetLayer(SerializedProperty layer)
    {
        layer.FindPropertyRelative("kind").enumValueIndex = 0;
        layer.FindPropertyRelative("scale").floatValue = 800f;
        layer.FindPropertyRelative("coverage").floatValue = 0.3f;
        layer.FindPropertyRelative("softness").floatValue = 0.3f;
        layer.FindPropertyRelative("heightOffset").floatValue = 0f;
        layer.FindPropertyRelative("hillMultiplier").floatValue = 1f;
        layer.FindPropertyRelative("detailMultiplier").floatValue = 1f;
        layer.FindPropertyRelative("decorationMultiplier").floatValue = 1f;
        layer.FindPropertyRelative("detailStrength").floatValue = 1f;
        layer.FindPropertyRelative("detailRangeUp").floatValue = 64f;
        layer.FindPropertyRelative("detailRangeDown").floatValue = 32f;
        layer.FindPropertyRelative("detailDensityScale").floatValue = 1f;
    }

    static string Summary(SerializedProperty layer)
    {
        var parts = new List<string> { ((SubBiomeKind)layer.FindPropertyRelative("kind").enumValueIndex).ToString() };
        var terrain = layer.FindPropertyRelative("detailTerrain").objectReferenceValue as TerrainTypeDefinitionSO;
        if (terrain != null)
            parts.Add($"3D: {terrain.terrainTypeName}");
        var decorations = layer.FindPropertyRelative("decorations");
        int count = 0;
        foreach (var list in new[] { "lSystems", "trees", "vegetation", "rocks", "aliens" })
            count += decorations.FindPropertyRelative(list)?.arraySize ?? 0;
        var mode = (SubBiomeDecorationMode)layer.FindPropertyRelative("decorationMode").enumValueIndex;
        if (count > 0 || mode == SubBiomeDecorationMode.Replace)
            parts.Add($"{count} decorations ({mode})");
        return string.Join(" · ", parts);
    }

    void DrawMaskAndEffects(SerializedProperty layer)
    {
        foreach (var name in new[] { "name", "kind", "scale", "coverage", "softness",
                     "heightOffset", "hillMultiplier", "detailMultiplier", "decorationMultiplier", "surfaceBlock" })
            Field(layer, name);
    }

    void DrawLayerShape(SerializedProperty layer)
    {
        Field(layer, "detailTerrain");
        bool own = layer.FindPropertyRelative("detailTerrain").objectReferenceValue != null;
        if (!own)
        {
            EditorGUILayout.HelpBox("No tree: the biome's own 3D shape applies here, scaled by Detail Multiplier.", MessageType.None);
            return;
        }
        Field(layer, "detailStrength");
        OverrideRow(layer, "overrideSurfaceOffset", "detailSurfaceOffset");
        OverrideRow(layer, "overrideRangeUp", "detailRangeUp");
        OverrideRow(layer, "overrideRangeDown", "detailRangeDown");
        Field(layer, "detailDensityScale");
    }

    // An override tick box and its value on one line (the value is measured when not ticked)
    static void OverrideRow(SerializedProperty parent, string toggleName, string valueName)
    {
        var toggle = parent.FindPropertyRelative(toggleName);
        var value = parent.FindPropertyRelative(valueName);
        using (new EditorGUILayout.HorizontalScope())
        {
            toggle.boolValue = EditorGUILayout.ToggleLeft(new GUIContent("Override", toggle.tooltip), toggle.boolValue, GUILayout.Width(72));
            using (new EditorGUI.DisabledScope(!toggle.boolValue))
                EditorGUILayout.PropertyField(value, new GUIContent(value.displayName + (toggle.boolValue ? "" : " (measured)"), value.tooltip));
        }
    }

    void DrawLayerDecorations(SerializedProperty layer)
    {
        Field(layer, "decorationMode");
        var decorations = layer.FindPropertyRelative("decorations");
        foreach (var name in new[] { "lSystems", "trees", "vegetation", "rocks", "aliens" })
            Field(decorations, name);
    }

    // ------------------------------------------------------------------------------------
    // Map
    // ------------------------------------------------------------------------------------

    void DrawMap()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(MapWidth)))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                onlyThisBiome = GUILayout.Toggle(onlyThisBiome, "This biome only", EditorStyles.miniButtonLeft);
                if (EditorGUI.EndChangeCheck())
                    UpdateMapBiome();
                EditorGUI.BeginChangeCheck();
                map.overlay = (RegionMapWindow.Overlay)EditorGUILayout.EnumPopup(map.overlay, EditorStyles.miniPullDown, GUILayout.Width(90));
                if (EditorGUI.EndChangeCheck())
                    map.Changed();
                GUI.enabled = map.CanRender;
                if (GUILayout.Button("Render", EditorStyles.miniButtonMid, GUILayout.Width(52)))
                    map.Generate();
                GUI.enabled = true;
                if (GUILayout.Button("Reset", EditorStyles.miniButtonRight, GUILayout.Width(44)))
                    map.ResetView(20000f);
            }
            if (!map.CanRender)
                EditorGUILayout.HelpBox("Needs a ChunkManager (or BiomeDataManager) in the open scene and a Region Gen Settings asset.", MessageType.Warning);
            EditorGUILayout.LabelField(map.Readout(), EditorStyles.miniLabel);
            var rect = GUILayoutUtility.GetRect(MapWidth, MapWidth, GUILayout.Width(MapWidth), GUILayout.Height(MapWidth));
            map.OnGUI(rect, this);
            if (!string.IsNullOrEmpty(map.Stats))
                EditorGUILayout.LabelField(map.Stats, EditorStyles.wordWrappedMiniLabel);
        }
    }
}
