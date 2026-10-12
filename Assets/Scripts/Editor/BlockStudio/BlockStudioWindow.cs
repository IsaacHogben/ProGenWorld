#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Window > ProGenWorld > Block Studio. Edits the BlockDatabase in one place: block properties,
/// the block's surface (grey texture, palette, patches), a live preview of a large area with the
/// scene's texture variation, and painters for the relief and alpha masks. While playing, every
/// change is pushed to the running world.
/// </summary>
public class BlockStudioWindow : EditorWindow
{
    enum Tab { Preview, Colour, Relief, Alpha }

    const string PendingBlockKey = "BlockStudio.PendingBlock";
    static readonly string[] DepthLabels = { "Surface", "Shallow", "Medium", "Deep" };
    static readonly float[] DepthValues = { 1f, 2f / 3f, 1f / 3f, 0f };

    [SerializeField] BlockDatabase database;
    [SerializeField] BlockTextureArrayBuilder builder;
    [SerializeField] int selected = 1;
    [SerializeField] Tab tab;
    [SerializeField] float previewBlocks = 48f;
    [SerializeField] bool previewRelief;
    [SerializeField] bool compareOriginal;
    [SerializeField] string search = "";
    [SerializeField] string newBlockName = "";
    [SerializeField] bool newBlockCopiesSurface = true;

    Vector2 listScroll, detailScroll;
    SerializedObject dbObject;
    SerializedObject surfaceObject;

    // Caches for the selected block
    BlockSurface loadedSurface;
    Color[] grey;                       // grey texture pixels: r shade, g colour position
    Color[] legacyPixels;
    Texture2D previewTexture, compareTexture;
    bool previewDirty = true;
    readonly TexelPainter colourPainter = new();
    readonly TexelPainter reliefPainter = new();
    readonly TexelPainter alphaPainter = new();
    bool reliefPainted, alphaPainted;   // false: showing the automatic relief / opaque mask
    readonly Dictionary<int, Texture2D> swatches = new();

    int TextureSize => builder != null ? builder.textureSize : 32;

    [MenuItem("Window/ProGenWorld/Block Studio")]
    public static BlockStudioWindow Open()
    {
        var w = GetWindow<BlockStudioWindow>("Block Studio");
        w.minSize = new Vector2(760f, 520f);
        return w;
    }

    public static void OpenFor(BlockDatabase db)
    {
        var w = Open();
        w.database = db;
        w.FindBuilder();
        w.Reload();
    }

    void OnEnable()
    {
        if (database == null)
            database = FindAsset<BlockDatabase>();
        FindBuilder();
        Undo.undoRedoPerformed += OnUndoRedo;
        EditorApplication.delayCall += ProcessPendingBlock;
        Reload();
    }

    void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        colourPainter.Dispose();
        reliefPainter.Dispose();
        alphaPainter.Dispose();
        DestroyImmediate(previewTexture);
        DestroyImmediate(compareTexture);
        ClearSwatches();
    }

    void OnUndoRedo()
    {
        Reload();
        Repaint();
        PushToWorld(false);
    }

    void FindBuilder()
    {
        if (builder != null && builder.blockDatabase == database)
            return;
        builder = null;
        foreach (var guid in AssetDatabase.FindAssets("t:BlockTextureArrayBuilder"))
        {
            var b = AssetDatabase.LoadAssetAtPath<BlockTextureArrayBuilder>(AssetDatabase.GUIDToAssetPath(guid));
            if (b != null && (b.blockDatabase == database || builder == null))
                builder = b;
        }
    }

    static T FindAsset<T>() where T : Object
    {
        foreach (var guid in AssetDatabase.FindAssets("t:" + typeof(T).Name))
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
            if (a != null)
                return a;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Selection caches
    // ------------------------------------------------------------------

    bool HasSelection => database != null && database.blocks != null && selected >= 0 && selected < database.blocks.Length;
    BlockDatabase.BlockEntry Entry => database.blocks[selected];

    void Reload()
    {
        dbObject = database != null ? new SerializedObject(database) : null;
        loadedSurface = null;
        surfaceObject = null;
        grey = null;
        legacyPixels = null;
        previewDirty = true;
        ClearSwatches();
        if (!HasSelection)
            return;

        var entry = Entry;
        int size = TextureSize;
        if (entry.texture != null)
            legacyPixels = BlockTextureArrayBuilder.ReadPixels(entry.texture, size);

        loadedSurface = entry.surface;
        if (loadedSurface == null)
            return;
        surfaceObject = new SerializedObject(loadedSurface);
        grey = loadedSurface.greyTexture != null
            ? BlockTextureArrayBuilder.ReadPixels(loadedSurface.greyTexture, size)
            : new Color[size * size];
        var positions = new float[grey.Length];
        for (int i = 0; i < grey.Length; i++)
            positions[i] = grey[i].g;
        colourPainter.Load(positions, size);

        reliefPainted = loadedSurface.reliefMask != null;
        reliefPainter.Load(reliefPainted ? BlockSurfaceAssets.ReadRed(loadedSurface.reliefMask, size) : AutoRelief(), size);

        var legacyMasks = BlockDatabase.LegacyAlphaMasks;
        int maskSize = legacyMasks != null ? legacyMasks.width : 16;
        alphaPainted = loadedSurface.alphaMask != null;
        if (alphaPainted)
            alphaPainter.Load(BlockSurfaceAssets.ReadRed(loadedSurface.alphaMask, maskSize), maskSize);
        else
        {
            var solid = new float[maskSize * maskSize];
            Array.Fill(solid, 1f);
            alphaPainter.Load(solid, maskSize);
        }
    }

    float[] AutoRelief()
    {
        int size = TextureSize;
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(grey[i].r, grey[i].r, grey[i].r, 1f);
        var bytes = new byte[pixels.Length];
        BlockTextureArrayBuilder.BuildHeights(pixels, bytes);
        var values = new float[bytes.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = bytes[i] / 255f;
        return values;
    }

    void Select(int index)
    {
        if (index == selected)
            return;
        selected = index;
        GUI.FocusControl(null);
        Reload();
    }

    // ------------------------------------------------------------------
    // Live update
    // ------------------------------------------------------------------

    /// <summary>Pushes the current database to the running world (play mode only).</summary>
    void PushToWorld(bool coloursOnly)
    {
        if (!EditorApplication.isPlaying)
            return;
        var manager = Object.FindFirstObjectByType<ChunkManager>();
        if (manager != null)
            manager.RefreshBlockVisuals(coloursOnly);
    }

    void SurfaceChanged(bool texturesChanged)
    {
        previewDirty = true;
        if (HasSelection)
            DropSwatch(selected);
        PushToWorld(!texturesChanged);
    }

    // ------------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------------

    void OnGUI()
    {
        DrawToolbar();
        if (database == null || database.blocks == null)
        {
            EditorGUILayout.HelpBox("Pick a BlockDatabase.", MessageType.Info);
            return;
        }
        if (dbObject == null || dbObject.targetObject != database)
            Reload();
        dbObject.Update();

        // The selected surface may have been swapped in the inspector.
        if (HasSelection && Entry.surface != loadedSurface)
            Reload();

        using (new EditorGUILayout.HorizontalScope())
        {
            DrawList();
            using (new EditorGUILayout.VerticalScope())
            {
                detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
                if (HasSelection)
                    DrawDetails();
                EditorGUILayout.EndScrollView();
            }
        }
    }

    void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUI.BeginChangeCheck();
            database = (BlockDatabase)EditorGUILayout.ObjectField(database, typeof(BlockDatabase), false, GUILayout.Width(180f));
            if (EditorGUI.EndChangeCheck())
            {
                FindBuilder();
                Reload();
            }

            if (GUILayout.Button(new GUIContent("Sync With Enum", "Adds entries for new BlockType values and orders the table by id."), EditorStyles.toolbarButton))
                SyncWithEnum(database, -1);

            if (GUILayout.Button(new GUIContent("Migrate Textures", "Converts every block that still uses a full colour texture into a surface (grey texture + palette) that matches it as closely as possible. Translucent blocks (water) keep their texture."), EditorStyles.toolbarButton))
                MigrateAll();

            if (GUILayout.Button(new GUIContent("Rebuild Arrays", "Rebuilds the block texture array (and pushes it to the running world while playing)."), EditorStyles.toolbarButton))
            {
                if (builder != null && !EditorApplication.isPlaying)
                    builder.GenerateTextureArray();
                PushToWorld(false);
            }

            GUILayout.FlexibleSpace();
            if (EditorApplication.isPlaying)
                GUILayout.Label(new GUIContent("● Live", "Changes are applied to the running world."), EditorStyles.miniBoldLabel);
        }
    }

    void MigrateAll()
    {
        if (database == null)
            return;
        if (!EditorUtility.DisplayDialog("Migrate block textures",
                "Every block with a colour texture and no surface gets a new surface in " + BlockSurfaceAssets.Root +
                ", fitted to look like its texture. The colour textures stay assigned as a reference. Continue?",
                "Migrate", "Cancel"))
            return;
        BlockSurfaceAssets.MigrateAll(database, TextureSize);
        if (builder != null && !EditorApplication.isPlaying)
            builder.GenerateTextureArray();
        PushToWorld(false);
        Reload();
    }

    void DrawList()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(230f)))
        {
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            listScroll = EditorGUILayout.BeginScrollView(listScroll);
            for (int i = 0; i < database.blocks.Length; i++)
            {
                var entry = database.blocks[i];
                string label = $"{i}  {entry.type}";
                if (!string.IsNullOrEmpty(search) && label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var rect = GUILayoutUtility.GetRect(200f, 22f, GUILayout.ExpandWidth(true));
                if (i == selected)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.9f, 0.35f));
                var swatch = Swatch(i);
                if (swatch != null)
                    GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 2f, 18f, 18f), swatch);
                string suffix = entry.surface != null ? "" : entry.texture != null ? "  (texture)" : "";
                GUI.Label(new Rect(rect.x + 26f, rect.y + 2f, rect.width - 26f, 18f), label + suffix);
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    Select(i);
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("New block type", EditorStyles.boldLabel);
            newBlockName = EditorGUILayout.TextField(newBlockName);
            newBlockCopiesSurface = EditorGUILayout.ToggleLeft(new GUIContent("Copy selected block", "The new block starts as a copy of the selected block, with its own copy of the surface."), newBlockCopiesSurface);
            using (new EditorGUI.DisabledScope(!ValidNewName(newBlockName, out _)))
            {
                if (GUILayout.Button(new GUIContent("Add Block Type", "Appends the name to the BlockType enum (BlockTypes.cs), recompiles and adds the block to the database.")))
                    AddBlockType(newBlockName.Trim(), newBlockCopiesSurface ? selected : -1);
            }
            if (!string.IsNullOrWhiteSpace(newBlockName) && !ValidNewName(newBlockName, out string why))
                EditorGUILayout.HelpBox(why, MessageType.None);
        }
    }

    void DrawDetails()
    {
        var entry = Entry;
        var element = dbObject.FindProperty("blocks").GetArrayElementAtIndex(selected);

        EditorGUILayout.LabelField($"{entry.type}  (id {selected})", EditorStyles.largeLabel);

        // --- Block properties ---
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(element.FindPropertyRelative("visibility"));
        using (new EditorGUILayout.HorizontalScope())
        {
            var solid = element.FindPropertyRelative("isSolid");
            var collision = element.FindPropertyRelative("hasCollision");
            solid.boolValue = EditorGUILayout.ToggleLeft("Solid", solid.boolValue, GUILayout.Width(80f));
            collision.boolValue = EditorGUILayout.ToggleLeft("Collision", collision.boolValue, GUILayout.Width(90f));
        }
        EditorGUILayout.PropertyField(element.FindPropertyRelative("surfaceDecoration"));
        if (entry.surface == null && entry.visibility == BlockVisibility.Stacked)
            EditorGUILayout.PropertyField(element.FindPropertyRelative("alphaMaskIndex"), new GUIContent("Legacy Alpha Mask"));
        if (EditorGUI.EndChangeCheck())
        {
            dbObject.ApplyModifiedProperties();
            PushToWorld(false);
        }

        EditorGUILayout.Space(6f);
        DrawSurfaceAssignment(element);

        if (loadedSurface == null)
        {
            EditorGUILayout.Space(6f);
            if (legacyPixels != null)
            {
                EditorGUILayout.HelpBox("This block still uses a full colour texture. Convert it to edit colours, relief and masks.", MessageType.Info);
                DrawPreviewArea(null);
            }
            return;
        }

        EditorGUILayout.Space(6f);
        DrawSurfaceSettings();

        EditorGUILayout.Space(6f);
        tab = (Tab)GUILayout.Toolbar((int)tab, new[] { "Preview", "Colour Map", "Relief", "Alpha Mask" });
        EditorGUILayout.Space(4f);
        switch (tab)
        {
            case Tab.Preview: DrawPreviewArea(loadedSurface); break;
            case Tab.Colour: DrawColourMap(); break;
            case Tab.Relief: DrawRelief(); break;
            case Tab.Alpha: DrawAlpha(); break;
        }
    }

    void DrawSurfaceAssignment(SerializedProperty element)
    {
        var entry = Entry;
        EditorGUILayout.LabelField("Surface", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(element.FindPropertyRelative("surface"), new GUIContent("Surface Asset", "Drag any surface here to reuse it; several blocks can share one."));
        if (entry.surface == null || legacyPixels != null)
            EditorGUILayout.PropertyField(element.FindPropertyRelative("texture"), new GUIContent(entry.surface == null ? "Colour Texture" : "Original Texture", "Legacy full colour texture. Kept as a reference for comparisons once the block has a surface."));
        if (EditorGUI.EndChangeCheck())
        {
            dbObject.ApplyModifiedProperties();
            Reload();
            PushToWorld(false);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("New", "A new surface with a noise grey texture and one colour.")))
                AssignSurface(BlockSurfaceAssets.CreateNew(entry.type.ToString(), TextureSize));
            using (new EditorGUI.DisabledScope(entry.surface == null))
            {
                if (GUILayout.Button(new GUIContent("Make Unique", "Gives this block its own copy of the surface (and of the textures that surface owns), so edits do not change other blocks.")))
                    AssignSurface(BlockSurfaceAssets.Duplicate(entry.surface, entry.type.ToString()));
            }
            using (new EditorGUI.DisabledScope(entry.texture == null))
            {
                if (GUILayout.Button(new GUIContent("Convert Texture", "Fits a grey texture and palette to the colour texture and assigns the new surface.")))
                {
                    var mask = entry.visibility == BlockVisibility.Stacked ? entry.alphaMaskIndex : (byte)0;
                    var s = BlockSurfaceAssets.ConvertTexture(entry.texture, entry.type.ToString(), TextureSize, mask, out float error);
                    Debug.Log($"[Block Studio] {entry.type}: converted with {s.ColourCount} colour(s), error {error * 255f:0.0}/255 RMS.");
                    AssignSurface(s);
                }
            }
            if (entry.surface != null && GUILayout.Button("Select Asset", GUILayout.Width(90f)))
            {
                Selection.activeObject = entry.surface;
                EditorGUIUtility.PingObject(entry.surface);
            }
        }

        if (entry.surface != null)
        {
            var users = new List<string>();
            for (int i = 0; i < database.blocks.Length; i++)
                if (i != selected && database.blocks[i].surface == entry.surface)
                    users.Add(database.blocks[i].type.ToString());
            if (users.Count > 0)
                EditorGUILayout.HelpBox("Shared with " + string.Join(", ", users) + ". Use Make Unique to edit this block alone.", MessageType.None);
        }
    }

    void AssignSurface(BlockSurface surface)
    {
        Undo.RecordObject(database, "Assign Block Surface");
        var entry = Entry;
        entry.surface = surface;
        database.blocks[selected] = entry;
        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();
        Reload();
        if (builder != null && !EditorApplication.isPlaying)
            builder.GenerateTextureArray();
        PushToWorld(false);
    }

    void DrawSurfaceSettings()
    {
        surfaceObject.Update();
        bool texturesChanged = false;

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(surfaceObject.FindProperty("greyTexture"));
        if (EditorGUI.EndChangeCheck())
            texturesChanged = true;

        EditorGUI.BeginChangeCheck();
        var colours = surfaceObject.FindProperty("colours");
        EditorGUILayout.LabelField(new GUIContent("Colours", "Patches take a colour along the list: the first where the large scale texture is dark, the last where it is bright."));
        for (int i = 0; i < colours.arraySize; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(colours.GetArrayElementAtIndex(i), new GUIContent(i == 0 ? "  Dark patches" : i == colours.arraySize - 1 ? "  Bright patches" : "  Middle"));
                using (new EditorGUI.DisabledScope(colours.arraySize <= 1))
                {
                    if (GUILayout.Button("-", GUILayout.Width(22f)))
                    {
                        colours.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }
        }
        using (new EditorGUI.DisabledScope(colours.arraySize >= BlockSurface.MaxColours))
        {
            if (GUILayout.Button("Add Colour", GUILayout.Width(100f)))
            {
                colours.InsertArrayElementAtIndex(colours.arraySize);
                if (colours.arraySize > 1)
                    colours.GetArrayElementAtIndex(colours.arraySize - 1).colorValue = colours.GetArrayElementAtIndex(colours.arraySize - 2).colorValue;
            }
        }

        EditorGUILayout.PropertyField(surfaceObject.FindProperty("shadeDark"));
        EditorGUILayout.PropertyField(surfaceObject.FindProperty("shadeLight"));
        EditorGUILayout.PropertyField(surfaceObject.FindProperty("patchContrast"));
        EditorGUILayout.PropertyField(surfaceObject.FindProperty("patchOffset"));
        EditorGUILayout.PropertyField(surfaceObject.FindProperty("texelColourVariation"));
        bool coloursChanged = EditorGUI.EndChangeCheck();

        if (coloursChanged || texturesChanged)
        {
            surfaceObject.ApplyModifiedProperties();
            if (texturesChanged)
                Reload();
            SurfaceChanged(texturesChanged);
        }
    }

    // --- Preview tab ---

    void DrawPreviewArea(BlockSurface surface)
    {
        var v = BlockStudioPreview.SceneVariation();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            previewBlocks = EditorGUILayout.Slider(new GUIContent("Area (blocks)", "Width of the previewed face in blocks. The large scale texture repeats every " + v.largeScale + " blocks."), previewBlocks, 2f, 128f);
            if (surface != null)
            {
                previewRelief = GUILayout.Toggle(previewRelief, new GUIContent("Relief", "Darken recessed texels."), "Button", GUILayout.Width(60f));
                using (new EditorGUI.DisabledScope(legacyPixels == null))
                    compareOriginal = GUILayout.Toggle(compareOriginal, new GUIContent("Compare", "Show the original colour texture next to the surface."), "Button", GUILayout.Width(70f));
            }
            if (EditorGUI.EndChangeCheck())
                previewDirty = true;
        }

        bool compare = surface == null || (compareOriginal && legacyPixels != null);
        bool both = surface != null && compare;
        float width = position.width - 230f - 30f;
        float side = Mathf.Clamp(both ? (width - 8f) * 0.5f : width, 128f, 640f);
        int res = Mathf.Clamp(Mathf.RoundToInt(previewBlocks * 16f), 64, 384);

        if (previewTexture == null || previewTexture.width != res)
        {
            DestroyImmediate(previewTexture);
            DestroyImmediate(compareTexture);
            previewTexture = BlockStudioPreview.NewTexture(res);
            compareTexture = BlockStudioPreview.NewTexture(res);
            previewDirty = true;
        }
        if (previewDirty)
        {
            if (surface != null && grey != null)
                BlockStudioPreview.RenderSurface(previewTexture, surface, grey, previewRelief ? reliefPainter.values : null, TextureSize, previewBlocks, v);
            if (legacyPixels != null)
                BlockStudioPreview.RenderLegacy(compareTexture, legacyPixels, TextureSize, previewBlocks, v);
            previewDirty = false;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (surface != null)
            {
                var r = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
                EditorGUI.DrawPreviewTexture(r, previewTexture);
            }
            if (compare && legacyPixels != null)
            {
                var r = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
                EditorGUI.DrawPreviewTexture(r, compareTexture);
            }
        }
        if (both)
            EditorGUILayout.LabelField("Surface (left) and original texture (right)", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"Small scale {v.smallScale} blocks, large scale {v.largeScale} blocks, large blend {v.largeBlend:0.##} ({(v.multiply ? "multiply" : "lerp")}), from the scene's Voxel Render Settings.", EditorStyles.miniLabel);
    }

    // --- Colour map tab ---

    void DrawColourMap()
    {
        var v = BlockStudioPreview.SceneVariation();
        int count = Mathf.Max(1, loadedSurface.ColourCount);
        EditorGUILayout.HelpBox("Where each colour goes: the grey texture's green channel, a position along the colours. " +
            "Up close it colours texels (with Texel Colour Variation), and at the large scale it decides which blocks form patches of which colour. " +
            "Left drag paints, right click picks.", MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            var labels = new string[count];
            var stops = new float[count];
            for (int i = 0; i < count; i++)
            {
                stops[i] = count == 1 ? 0f : i / (count - 1f);
                labels[i] = "Colour " + (i + 1);
            }
            int level = Array.FindIndex(stops, d => Mathf.Abs(d - colourPainter.brushValue) < 0.02f);
            int picked = GUILayout.Toolbar(level, labels);
            if (picked != level && picked >= 0)
                colourPainter.brushValue = stops[picked];
            colourPainter.brushValue = EditorGUILayout.Slider(colourPainter.brushValue, 0f, 1f, GUILayout.Width(160f));
            var swatch = GUILayoutUtility.GetRect(22f, 18f, GUILayout.Width(22f));
            EditorGUI.DrawRect(swatch, loadedSurface.Colour(colourPainter.brushValue, 0.5f));
        }
        colourPainter.brushSize = EditorGUILayout.IntSlider("Brush Size", colourPainter.brushSize, 1, 5);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("From Shade", "Colour position = shade, like a gradient map.")))
            {
                var q = new float[grey.Length];
                for (int i = 0; i < q.Length; i++)
                    q[i] = grey[i].r;
                ApplyColourMap(q);
            }
            if (GUILayout.Button(new GUIContent("Snap To Colours", "Snap every texel to the nearest colour.")))
            {
                var q = (float[])colourPainter.values.Clone();
                int steps = Mathf.Max(1, count - 1);
                for (int i = 0; i < q.Length; i++)
                    q[i] = Mathf.Round(q[i] * steps) / steps;
                ApplyColourMap(q);
            }
            if (GUILayout.Button("Invert"))
            {
                var q = (float[])colourPainter.values.Clone();
                for (int i = 0; i < q.Length; i++)
                    q[i] = 1f - q[i];
                ApplyColourMap(q);
            }
            using (new EditorGUI.DisabledScope(!colourPainter.CanUndo))
                if (GUILayout.Button("Undo Stroke"))
                {
                    colourPainter.Undo();
                    SaveColourMap();
                }
        }

        float side = Mathf.Clamp(position.width - 230f - 40f, 200f, 560f);
        var rect = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
        if (colourPainter.Draw(rect, (i, t) => loadedSurface.Colour(t, grey[i].r), colourPainter.size / v.smallScale))
            SaveColourMap();
        EditorGUILayout.LabelField($"One tile = {v.smallScale} x {v.smallScale} blocks. At the large scale each texel covers {v.largeScale / colourPainter.size:0.#} blocks.", EditorStyles.miniLabel);
    }

    void ApplyColourMap(float[] values)
    {
        colourPainter.Set(values);
        SaveColourMap();
    }

    void SaveColourMap()
    {
        for (int i = 0; i < grey.Length; i++)
            grey[i] = new Color(grey[i].r, colourPainter.values[i], grey[i].r, 1f);
        BlockSurfaceAssets.SaveOwnedPixels(loadedSurface, BlockSurfaceAssets.Kind.Grey, grey, TextureSize);
        colourPainter.dirty = false;
        colourPainter.MarkDisplayDirty();
        reliefPainter.MarkDisplayDirty();
        alphaPainter.MarkDisplayDirty();
        SurfaceChanged(true);
    }

    // --- Relief tab ---

    void DrawRelief()
    {
        var v = BlockStudioPreview.SceneVariation();
        EditorGUILayout.HelpBox(reliefPainted
            ? "Painted relief. Left drag paints, right click picks a depth. Strokes wrap around the tile edges."
            : "Automatic relief from the grey texture's brightness. Painting saves it as this surface's relief mask.", MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            int level = Array.FindIndex(DepthValues, d => Mathf.Abs(d - reliefPainter.brushValue) < 0.02f);
            int picked = GUILayout.Toolbar(level, DepthLabels);
            if (picked != level && picked >= 0)
                reliefPainter.brushValue = DepthValues[picked];
            reliefPainter.brushValue = EditorGUILayout.Slider(reliefPainter.brushValue, 0f, 1f, GUILayout.Width(160f));
        }
        reliefPainter.brushSize = EditorGUILayout.IntSlider("Brush Size", reliefPainter.brushSize, 1, 5);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("From Grey", "Recreate the relief from the grey texture's brightness, as the automatic relief does.")))
                ApplyRelief(AutoRelief());
            if (GUILayout.Button(new GUIContent("Quantise", "Snap every texel to the nearest of the four depth levels.")))
            {
                var q = (float[])reliefPainter.values.Clone();
                for (int i = 0; i < q.Length; i++)
                    q[i] = Mathf.Round(q[i] * 3f) / 3f;
                ApplyRelief(q);
            }
            if (GUILayout.Button("Invert"))
            {
                var q = (float[])reliefPainter.values.Clone();
                for (int i = 0; i < q.Length; i++)
                    q[i] = 1f - q[i];
                ApplyRelief(q);
            }
            if (GUILayout.Button("Flat"))
            {
                var q = new float[reliefPainter.values.Length];
                Array.Fill(q, 1f);
                ApplyRelief(q);
            }
            using (new EditorGUI.DisabledScope(!reliefPainter.CanUndo))
                if (GUILayout.Button("Undo Stroke"))
                {
                    reliefPainter.Undo();
                    SaveRelief();
                }
            using (new EditorGUI.DisabledScope(!reliefPainted))
                if (GUILayout.Button(new GUIContent("Use Automatic", "Remove the painted relief mask from the surface (the PNG stays on disk).")))
                {
                    Undo.RecordObject(loadedSurface, "Remove Relief Mask");
                    loadedSurface.reliefMask = null;
                    EditorUtility.SetDirty(loadedSurface);
                    Reload();
                    SurfaceChanged(true);
                }
        }

        int size = reliefPainter.size;
        float side = Mathf.Clamp(position.width - 230f - 40f, 200f, 560f);
        var rect = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
        float texelsPerBlock = size / v.smallScale;
        if (reliefPainter.Draw(rect, ReliefColour, texelsPerBlock))
            SaveRelief();
        EditorGUILayout.LabelField($"One tile = {v.smallScale} x {v.smallScale} blocks (white lines are block edges). Darker = deeper.", EditorStyles.miniLabel);
    }

    Color ReliefColour(int i, float h)
    {
        var g = grey != null && i < grey.Length ? grey[i] : Color.grey;
        var c = loadedSurface.Evaluate(g.r, g.g, g.g) * Mathf.Lerp(0.25f, 1f, h);
        c.a = 1f;
        return c;
    }

    void ApplyRelief(float[] values)
    {
        reliefPainter.Set(values);
        SaveRelief();
    }

    void SaveRelief()
    {
        BlockSurfaceAssets.SaveOwned(loadedSurface, BlockSurfaceAssets.Kind.Relief, reliefPainter.values, reliefPainter.size);
        reliefPainted = true;
        reliefPainter.dirty = false;
        reliefPainter.MarkDisplayDirty();
        SurfaceChanged(true);
    }

    // --- Alpha tab ---

    void DrawAlpha()
    {
        if (Entry.visibility != BlockVisibility.Stacked)
            EditorGUILayout.HelpBox("Alpha masks only cut holes in Stacked blocks (leaves). Set the visibility to Stacked to see it in the world.", MessageType.Info);
        EditorGUILayout.HelpBox(alphaPainted
            ? "One tile = one block face. Left drag paints, right click picks."
            : "No alpha mask: the block is opaque. Painting creates a mask for this surface.", MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            int picked = GUILayout.Toolbar(alphaPainter.brushValue > 0.5f ? 0 : 1, new[] { "Solid", "Hole" });
            alphaPainter.brushValue = picked == 0 ? 1f : 0f;
        }
        alphaPainter.brushSize = EditorGUILayout.IntSlider("Brush Size", alphaPainter.brushSize, 1, 5);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Fill"))
            {
                var q = new float[alphaPainter.values.Length];
                Array.Fill(q, 1f);
                ApplyAlpha(q);
            }
            if (GUILayout.Button("Invert"))
            {
                var q = (float[])alphaPainter.values.Clone();
                for (int i = 0; i < q.Length; i++)
                    q[i] = q[i] > 0.5f ? 0f : 1f;
                ApplyAlpha(q);
            }
            if (GUILayout.Button(new GUIContent("Random Holes", "Fills the mask and punches random holes (about a third of the texels).")))
            {
                var rng = new System.Random(Environment.TickCount);
                var q = new float[alphaPainter.values.Length];
                for (int i = 0; i < q.Length; i++)
                    q[i] = rng.NextDouble() < 0.33 ? 0f : 1f;
                ApplyAlpha(q);
            }
            using (new EditorGUI.DisabledScope(!alphaPainter.CanUndo))
                if (GUILayout.Button("Undo Stroke"))
                {
                    alphaPainter.Undo();
                    SaveAlpha();
                }
            using (new EditorGUI.DisabledScope(!alphaPainted))
                if (GUILayout.Button(new GUIContent("Remove Mask", "Make the block opaque again (the PNG stays on disk).")))
                {
                    Undo.RecordObject(loadedSurface, "Remove Alpha Mask");
                    loadedSurface.alphaMask = null;
                    EditorUtility.SetDirty(loadedSurface);
                    Reload();
                    SurfaceChanged(true);
                }
        }

        float side = Mathf.Clamp(position.width - 230f - 40f, 200f, 480f);
        var rect = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
        if (alphaPainter.Draw(rect, AlphaColour, 0f))
            SaveAlpha();
    }

    Color AlphaColour(int i, float a)
    {
        int size = alphaPainter.size;
        int x = i % size, y = i / size;
        if (a <= 0.5f)
            return ((x + y) & 1) == 0 ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.22f, 0.22f, 0.22f);
        if (grey == null)
            return Color.white;
        int ts = TextureSize;
        var g = grey[(x * ts / size) + (y * ts / size) * ts];
        var c = loadedSurface.Evaluate(g.r, g.g, g.g);
        c.a = 1f;
        return c;
    }

    void ApplyAlpha(float[] values)
    {
        alphaPainter.Set(values);
        SaveAlpha();
    }

    void SaveAlpha()
    {
        BlockSurfaceAssets.SaveOwned(loadedSurface, BlockSurfaceAssets.Kind.Alpha, alphaPainter.values, alphaPainter.size);
        alphaPainted = true;
        alphaPainter.dirty = false;
        alphaPainter.MarkDisplayDirty();
        SurfaceChanged(true);
    }

    // ------------------------------------------------------------------
    // Swatches
    // ------------------------------------------------------------------

    Texture2D Swatch(int i)
    {
        if (swatches.TryGetValue(i, out var tex))
            return tex;
        var entry = database.blocks[i];
        const int size = 16;
        Color[] pixels = null;
        if (entry.surface != null && entry.surface.greyTexture != null)
        {
            var g = BlockTextureArrayBuilder.ReadPixels(entry.surface.greyTexture, size);
            pixels = entry.surface.ColourTile(g);
        }
        else if (entry.texture != null)
            pixels = BlockTextureArrayBuilder.ReadPixels(entry.texture, size);

        tex = null;
        if (pixels != null)
        {
            tex = BlockStudioPreview.NewTexture(size);
            tex.SetPixels(pixels);
            tex.Apply(false);
        }
        swatches[i] = tex;
        return tex;
    }

    void DropSwatch(int i)
    {
        if (swatches.TryGetValue(i, out var tex))
            DestroyImmediate(tex);
        swatches.Remove(i);
        // Blocks sharing the surface change too.
        if (database != null && i < database.blocks.Length && database.blocks[i].surface != null)
            for (int j = 0; j < database.blocks.Length; j++)
                if (j != i && database.blocks[j].surface == database.blocks[i].surface && swatches.TryGetValue(j, out var other))
                {
                    DestroyImmediate(other);
                    swatches.Remove(j);
                }
    }

    void ClearSwatches()
    {
        foreach (var t in swatches.Values)
            DestroyImmediate(t);
        swatches.Clear();
    }

    // ------------------------------------------------------------------
    // Block types
    // ------------------------------------------------------------------

    static bool ValidNewName(string name, out string why)
    {
        name = name?.Trim() ?? "";
        why = null;
        if (name.Length == 0) { why = "Enter a name."; return false; }
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) { why = "Letters, digits and _ only, not starting with a digit."; return false; }
        if (Enum.IsDefined(typeof(BlockType), name)) { why = "That block type exists already."; return false; }
        if (Enum.GetValues(typeof(BlockType)).Length >= VoxelGpuFormat.GrassMarker) { why = "No block ids left."; return false; }
        return true;
    }

    static string BlockTypesPath()
    {
        foreach (var guid in AssetDatabase.FindAssets("BlockTypes t:MonoScript"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (File.ReadAllText(path).Contains("enum BlockType"))
                return path;
        }
        return null;
    }

    void AddBlockType(string name, int copyFrom)
    {
        string path = BlockTypesPath();
        if (path == null)
        {
            Debug.LogError("[Block Studio] Could not find the BlockType enum (BlockTypes.cs).");
            return;
        }

        string text = File.ReadAllText(path);
        int start = text.IndexOf("enum BlockType", StringComparison.Ordinal);
        int open = text.IndexOf('{', start);
        int close = text.IndexOf('}', open);
        string body = text.Substring(open + 1, close - open - 1).TrimEnd();
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (!body.EndsWith(","))
            body += ",";
        body += newline + "    " + name + newline;
        text = text.Substring(0, open + 1) + body + text.Substring(close);
        File.WriteAllText(path, text);

        SessionState.SetString(PendingBlockKey, $"{name}|{copyFrom}|{AssetDatabase.GetAssetPath(database)}");
        newBlockName = "";
        AssetDatabase.ImportAsset(path);
        AssetDatabase.Refresh();
    }

    /// <summary>After the recompile for a new block type: adds it to the database (as a copy of the chosen block).</summary>
    void ProcessPendingBlock()
    {
        string pending = SessionState.GetString(PendingBlockKey, "");
        if (string.IsNullOrEmpty(pending) || EditorApplication.isCompiling)
            return;
        var parts = pending.Split('|');
        if (parts.Length < 3 || !Enum.TryParse(parts[0], out BlockType type))
            return;   // not compiled yet (or failed): try again after the next reload
        SessionState.EraseString(PendingBlockKey);

        var db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(parts[2]);
        if (db == null)
            return;
        database = db;
        int copyFrom = int.TryParse(parts[1], out int c) ? c : -1;
        SyncWithEnum(db, copyFrom >= 0 && copyFrom < db.blocks.Length ? copyFrom : -1, type);
        selected = (int)type;
        FindBuilder();
        Reload();
        Repaint();
    }

    /// <summary>
    /// Orders the database by BlockType id, adding entries for new values. A new entry for
    /// newType copies the block at copyFrom (with its own copy of the surface).
    /// </summary>
    public static void SyncWithEnum(BlockDatabase db, int copyFrom, BlockType? newType = null)
    {
        Undo.RecordObject(db, "Sync Block Database");
        var values = (BlockType[])Enum.GetValues(typeof(BlockType));
        var list = new BlockDatabase.BlockEntry[values.Length];
        var old = db.blocks ?? Array.Empty<BlockDatabase.BlockEntry>();
        for (int i = 0; i < values.Length; i++)
        {
            int existing = Array.FindIndex(old, b => b.type == values[i] && b.name != null);
            if (existing >= 0)
            {
                list[i] = old[existing];
                continue;
            }

            if (newType.HasValue && values[i] == newType.Value && copyFrom >= 0 && copyFrom < old.Length)
            {
                var copy = old[copyFrom];
                copy.type = values[i];
                copy.name = values[i].ToString();
                if (copy.surface != null)
                    copy.surface = BlockSurfaceAssets.Duplicate(copy.surface, copy.name);
                list[i] = copy;
            }
            else
                list[i] = new BlockDatabase.BlockEntry(values[i]);
        }
        db.blocks = list;
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Block Studio] Synced {values.Length} entries from the BlockType enum.");
    }
}

/// <summary>Pushes surface edits made in the inspector to the running world.</summary>
[InitializeOnLoad]
static class BlockSurfaceLiveUpdate
{
    static BlockSurfaceLiveUpdate()
    {
        BlockSurface.Changed += _ =>
        {
            if (!EditorApplication.isPlaying)
                return;
            var manager = Object.FindFirstObjectByType<ChunkManager>();
            if (manager != null)
                manager.RefreshBlockVisuals(true);
        };
    }
}
#endif
