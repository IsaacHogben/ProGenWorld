using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.Rendering;

// ----------------------------------------------------------------------------
// Editor window that runs the real LSystemInterpreter on an
// LSystemScriptableObject and shows the resulting blocks as a voxel mesh,
// regenerating live as the asset is edited (here or in the normal inspector).
//
// Open via Tools > L-System Tree Preview, the button on the asset's
// inspector, or by double-clicking an L-System asset.
//
// Rendering goes straight through a CommandBuffer into a RenderTexture, so it
// does not depend on the active render pipeline.
// ----------------------------------------------------------------------------
public class LSystemPreviewWindow : EditorWindow
{
    const float SidebarWidth = 360f;
    const float FieldOfView = 30f;
    static readonly Color Background = new Color(0.19f, 0.21f, 0.24f);

    // The interpreter writes through BlockWriter. With a chunk size of 1 every block's local
    // position is 0, which BlockWriter never writes directly, so every write lands in pendingWrites.
    const int CaptureChunkSize = 1;
    static readonly int3 CaptureChunk = int3.zero;

    [SerializeField] LSystemScriptableObject tree;
    [SerializeField] int seed = 1;
    [SerializeField] int variants = 1;
    [SerializeField] float scale = 1f;
    [SerializeField] bool followSelection = true;
    [SerializeField] bool liveUpdate = true;
    [SerializeField] bool autoFrame = true;
    [SerializeField] bool showGround = true;
    [SerializeField] bool showTextures = true;
    [SerializeField] bool hillyGround;
    [SerializeField] bool showSymbolHelp;
    [SerializeField] Vector2 orbit = new Vector2(35f, 20f); // yaw, pitch (degrees)

    SerializedObject serialized;
    Vector2 sidebarScroll;
    RenderTexture renderTarget;
    CommandBuffer commands;
    Material material;
    Mesh treeMesh, groundMesh;
    Bounds bounds;
    Vector3 pivot;
    float distance = -1f;
    string lastJson;
    string status;
    Stats stats;

    // Built from the BlockDatabase: average colour per block, and a texture array slice per block.
    bool blockResourcesBuilt;
    Dictionary<BlockType, Color> palette;
    Dictionary<BlockType, int> slices;
    Texture2DArray blockTextures;

    // Ground under each variant: x offset applied when meshing, and the variant's local bounds.
    readonly List<(Vector3 shift, int3 min, int3 max)> groundPatches = new List<(Vector3, int3, int3)>();

    struct Stats
    {
        public int Blocks, BodyBlocks, FoliageBlocks, Writes;
        public Vector3Int Size;
        public long Ops;
        public double Milliseconds;
    }

    // ------------------------------------------------------------------
    // Opening
    // ------------------------------------------------------------------
    [MenuItem("Tools/L-System Tree Preview")]
    public static void OpenFromMenu() => Open(Selection.activeObject as LSystemScriptableObject);

    public static void Open(LSystemScriptableObject asset)
    {
        var window = GetWindow<LSystemPreviewWindow>("L-System Preview");
        window.minSize = new Vector2(700f, 400f);
        if (asset != null)
            window.SetTree(asset);
    }

    [OnOpenAsset]
    static bool OnOpenAsset(int instanceId, int line)
    {
        if (EditorUtility.InstanceIDToObject(instanceId) is LSystemScriptableObject asset)
        {
            Open(asset);
            return true;
        }
        return false;
    }

    void SetTree(LSystemScriptableObject asset)
    {
        if (tree == asset && treeMesh != null)
            return;
        tree = asset;
        serialized = asset != null ? new SerializedObject(asset) : null;
        distance = -1f;
        Regenerate();
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------
    void OnEnable()
    {
        if (tree != null)
            Regenerate();
    }

    void OnDisable()
    {
        commands?.Release();
        commands = null;
        if (renderTarget != null)
        {
            renderTarget.Release();
            DestroyImmediate(renderTarget);
        }
        DestroyImmediate(material);
        DestroyImmediate(treeMesh);
        DestroyImmediate(groundMesh);
        DestroyImmediate(blockTextures);
    }

    void OnSelectionChange()
    {
        if (followSelection && Selection.activeObject is LSystemScriptableObject asset && asset != tree)
        {
            SetTree(asset);
            Repaint();
        }
    }

    // Picks up edits made anywhere (this window, the normal inspector, undo/redo).
    void OnInspectorUpdate()
    {
        if (!liveUpdate || tree == null)
            return;
        if (EditorJsonUtility.ToJson(tree) != lastJson)
        {
            Regenerate();
            Repaint();
        }
    }

    // ------------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------------
    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(SidebarWidth)))
            {
                sidebarScroll = EditorGUILayout.BeginScrollView(sidebarScroll);
                DrawSidebar();
                EditorGUILayout.EndScrollView();
            }

            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPreview(rect);
        }
    }

    void DrawSidebar()
    {
        float oldLabelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 140f;

        EditorGUILayout.Space(4f);
        var picked = (LSystemScriptableObject)EditorGUILayout.ObjectField("Tree Asset", tree, typeof(LSystemScriptableObject), false);
        if (picked != tree)
            SetTree(picked);
        followSelection = EditorGUILayout.Toggle(new GUIContent("Follow Selection", "Switch to an L-System asset when it is selected in the Project window."), followSelection);

        if (tree == null)
        {
            EditorGUILayout.HelpBox("Select or assign an LSystemScriptableObject to preview it.", MessageType.Info);
            EditorGUIUtility.labelWidth = oldLabelWidth;
            return;
        }

        DrawPreviewSettings();
        DrawStats();
        DrawAssetFields();
        DrawSymbolHelp();

        EditorGUIUtility.labelWidth = oldLabelWidth;
    }

    void DrawPreviewSettings()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        using (new EditorGUILayout.HorizontalScope())
        {
            seed = EditorGUILayout.IntField("Seed", seed);
            if (GUILayout.Button("Random", GUILayout.Width(64f)))
                seed = UnityEngine.Random.Range(1, int.MaxValue);
        }
        variants = EditorGUILayout.IntSlider(new GUIContent("Variants", "Shows several trees side by side, using consecutive seeds."), variants, 1, 8);
        scale = EditorGUILayout.Slider(new GUIContent("Scale", "Size multiplier, as set per biome entry (Min/Max Scale)."), scale, 0.1f, 2f);
        bool regenerate = EditorGUI.EndChangeCheck();

        liveUpdate = EditorGUILayout.Toggle("Live Update", liveUpdate);
        autoFrame = EditorGUILayout.Toggle(new GUIContent("Auto Frame", "Re-centre the camera whenever the tree is regenerated."), autoFrame);
        showGround = EditorGUILayout.Toggle("Show Ground", showGround);
        EditorGUI.BeginChangeCheck();
        hillyGround = EditorGUILayout.Toggle(new GUIContent("Hilly Ground", "Test terrain for roots and ground scatter, which settle onto the surface."), hillyGround);
        if (EditorGUI.EndChangeCheck())
            regenerate = true;
        showTextures = EditorGUILayout.Toggle(new GUIContent("Show Textures", "Off shows each block as its average texture colour."), showTextures);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Regenerate", "Also reloads block textures from the BlockDatabase.")))
            {
                blockResourcesBuilt = false;
                regenerate = true;
            }
            if (GUILayout.Button("Frame"))
                Frame();
        }

        if (regenerate)
            Regenerate();
    }

    void DrawStats()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);

        if (!string.IsNullOrEmpty(status))
        {
            EditorGUILayout.HelpBox(status, MessageType.Warning);
            return;
        }

        int n = Mathf.Max(1, variants);
        string per = variants > 1 ? " (avg per tree)" : "";
        EditorGUILayout.LabelField(new GUIContent("Symbols (worst case)", "Upper bound on symbols the interpreter executes, taking the longest option of every random rule."), new GUIContent(stats.Ops.ToString("N0")));
        EditorGUILayout.LabelField("Blocks" + per, $"{stats.Blocks / n:N0}  ({stats.BodyBlocks / n:N0} body, {stats.FoliageBlocks / n:N0} foliage)");
        EditorGUILayout.LabelField(new GUIContent("Block Writes" + per, "Writes the tree hands to the chunk system - the main runtime cost. Includes blocks that end up inside terrain."), new GUIContent((stats.Writes / n).ToString("N0")));
        EditorGUILayout.LabelField("Size (X × Y × Z)", $"{stats.Size.x} × {stats.Size.y} × {stats.Size.z}");
        EditorGUILayout.LabelField("Generation Time" + per, $"{stats.Milliseconds / n:0.0} ms");
    }

    void DrawAssetFields()
    {
        if (serialized == null || serialized.targetObject != tree)
            serialized = new SerializedObject(tree);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Asset", EditorStyles.boldLabel);

        serialized.Update();
        var property = serialized.GetIterator();
        bool enterChildren = true;
        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (property.propertyPath == "m_Script")
                continue;
            EditorGUILayout.PropertyField(property, true);
        }
        if (serialized.ApplyModifiedProperties() && liveUpdate)
            Regenerate();
    }

    void DrawSymbolHelp()
    {
        EditorGUILayout.Space(6f);
        showSymbolHelp = EditorGUILayout.Foldout(showSymbolHelp, "Symbol Reference", true);
        if (!showSymbolHelp)
            return;

        EditorGUILayout.HelpBox(
            "Most symbols take an optional value, e.g. F(0.5) or +(15).\n\n" +
            "F(x)  forward x steps, placing body blocks\n" +
            "f(x)  forward x steps, no blocks\n" +
            "+ -  spin around the heading by ±YawAngle (or degrees)\n" +
            "& ^  tilt by ±PitchAngle\n" +
            "\\ /  tilt sideways by ±RollAngle\n" +
            "~(deg)  tilt by CurveAngle with half jitter - repeat to curve\n" +
            "|  spin 180°\n" +
            "[ ]  push / pop state (] places foliage at leaf tips)\n" +
            "!(x)  advance taper depth by x\n" +
            "*(x)  scale step length for this branch (default 0.8)\n" +
            "T(x)  place foliage here, x scales its size\n" +
            "Q  suppress tip foliage for this branch\n\n" +
            "Other characters expand by their rules; several rules with the same symbol are picked randomly by Weight.\n" +
            "+ and - only spin around the heading, so tilt (& or ^) to leave vertical.",
            MessageType.None);
    }

    // ------------------------------------------------------------------
    // Preview rendering + camera
    // ------------------------------------------------------------------
    void DrawPreview(Rect rect)
    {
        if (Event.current.type == EventType.Repaint && rect.width > 1f && rect.height > 1f)
        {
            RenderPreview(rect);
            GUI.DrawTexture(rect, renderTarget, ScaleMode.StretchToFill, false);

            var hint = new Rect(rect.x + 8f, rect.yMax - 22f, rect.width - 16f, 18f);
            GUI.Label(hint, "Left drag: orbit   •   Right/middle drag: pan   •   Scroll: zoom   •   F: frame", EditorStyles.whiteMiniLabel);
            if (material == null)
                EditorGUI.HelpBox(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 40f),
                    "Shader 'Hidden/LSystemPreview' not found. Check LSystemPreview.shader compiled.", MessageType.Error);
        }

        HandleInput(rect);
    }

    void RenderPreview(Rect rect)
    {
        EnsureRenderResources();

        float scale = EditorGUIUtility.pixelsPerPoint;
        int width = Mathf.Max(1, Mathf.RoundToInt(rect.width * scale));
        int height = Mathf.Max(1, Mathf.RoundToInt(rect.height * scale));
        if (renderTarget == null || renderTarget.width != width || renderTarget.height != height)
        {
            if (renderTarget != null)
            {
                renderTarget.Release();
                DestroyImmediate(renderTarget);
            }
            renderTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "LSystemPreviewTarget",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 4,
            };
            renderTarget.Create();
        }

        var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
        float dist = distance > 0f ? distance : 20f;
        var cameraPos = pivot - rotation * Vector3.forward * dist;
        // Camera space looks down -Z, hence the flipped Z scale (matches Camera.worldToCameraMatrix).
        var view = Matrix4x4.TRS(cameraPos, rotation, new Vector3(1f, 1f, -1f)).inverse;
        float near = Mathf.Max(0.05f, dist * 0.01f);
        float far = dist * 4f + bounds.size.magnitude * 2f + 100f;
        var projection = Matrix4x4.Perspective(FieldOfView, width / (float)height, near, far);

        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        commands.Clear();
        commands.SetRenderTarget(renderTarget);
        commands.ClearRenderTarget(true, true, linear ? Background.linear : Background);
        if (material != null)
        {
            material.SetFloat("_UseTextures", showTextures && blockTextures != null ? 1f : 0f);
            if (blockTextures != null)
                material.SetTexture("_Blocks", blockTextures);

            commands.SetViewProjectionMatrices(view, projection);
            if (showGround && groundMesh != null)
                commands.DrawMesh(groundMesh, Matrix4x4.identity, material, 0, 0);
            if (treeMesh != null)
                commands.DrawMesh(treeMesh, Matrix4x4.identity, material, 0, 0);
        }
        Graphics.ExecuteCommandBuffer(commands);
    }

    void HandleInput(Rect rect)
    {
        var e = Event.current;
        int id = GUIUtility.GetControlID(FocusType.Passive, rect);

        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (rect.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = id;
                    e.Use();
                }
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id)
                    break;
                if (e.button == 0 && !e.alt)
                {
                    orbit.x += e.delta.x * 0.4f;
                    orbit.y = Mathf.Clamp(orbit.y + e.delta.y * 0.4f, -89f, 89f);
                }
                else
                {
                    var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
                    float k = Mathf.Max(distance, 1f) * 0.0025f;
                    pivot += rotation * new Vector3(-e.delta.x * k, e.delta.y * k, 0f);
                }
                e.Use();
                Repaint();
                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
                break;

            case EventType.ScrollWheel:
                if (rect.Contains(e.mousePosition))
                {
                    distance = Mathf.Clamp(distance * (1f + e.delta.y * 0.04f), 1f, 5000f);
                    e.Use();
                    Repaint();
                }
                break;

            case EventType.KeyDown:
                if (e.keyCode == KeyCode.F && rect.Contains(e.mousePosition))
                {
                    Frame();
                    e.Use();
                }
                break;
        }
    }

    void Frame()
    {
        pivot = bounds.center;
        float radius = Mathf.Max(bounds.extents.magnitude, 2f);
        distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
        Repaint();
    }

    void EnsureRenderResources()
    {
        if (commands == null)
            commands = new CommandBuffer { name = "L-System Preview" };
        if (material == null)
        {
            var shader = Shader.Find("Hidden/LSystemPreview");
            if (shader != null)
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
    }

    // ------------------------------------------------------------------
    // Generation
    // ------------------------------------------------------------------
    void Regenerate()
    {
        status = null;
        stats = default;
        if (tree == null)
        {
            DestroyImmediate(treeMesh);
            DestroyImmediate(groundMesh);
            return;
        }

        lastJson = EditorJsonUtility.ToJson(tree);
        if (!blockResourcesBuilt)
            BuildBlockResources();

        var data = tree.ToTreeData(out string error);
        if (error != null)
        {
            status = error;
            DestroyImmediate(treeMesh);
            return;
        }

        var atScale = data;
        atScale.Iterations += (int)math.ceil(LSystemInterpreter.ExtraIterations(in data, scale));
        stats.Ops = LSystemCompiler.WorstCaseOps(in atScale, LSystemInterpreter.MaxExecutedOps + 1L);
        if (stats.Ops > LSystemInterpreter.MaxExecutedOps)
        {
            status = $"Expands past {LSystemInterpreter.MaxExecutedOps:N0} symbols, so it would be cut short in game. Lower Iterations or simplify the rules.";
            DestroyImmediate(treeMesh);
            return;
        }

        var sw = Stopwatch.StartNew();
        var captures = new List<Dictionary<int3, byte>>(variants);
        for (int i = 0; i < variants; i++)
            captures.Add(Capture(in data, (uint)(seed + i), scale, hillyGround, ref stats.Writes));
        stats.Milliseconds = sw.Elapsed.TotalMilliseconds;

        BuildMeshes(captures, data.BodyBlock);

        if (autoFrame || distance < 0f)
            Frame();
    }

    // Height of the topmost ground block at (x, z), relative to the block the tree spawns on.
    static int GroundHeight(int x, int z, bool hilly)
    {
        if (!hilly)
            return 0;
        static int Raw(int x, int z) => Mathf.RoundToInt(4f * Mathf.Sin(x * 0.09f) + 3f * Mathf.Cos(z * 0.07f + 1f) + 2f * Mathf.Sin((x + z) * 0.15f));
        return Raw(x, z) - Raw(0, 0);
    }

    // Applies the tree's writes in order the way the game does: ReplaceAir skips anything already
    // there (terrain included), and DropToSurface settles onto the first solid block below it,
    // or climbs out if it starts underground, searching up to 16 blocks.
    static Dictionary<int3, byte> Capture(in LSystemTreeData data, uint seed, float scale, bool hilly, ref int writeCount)
    {
        var writes = new NativeList<PendingBlockWrite>(1024, Allocator.Persistent);
        var blockIds = new NativeArray<byte>(1, Allocator.Persistent);
        try
        {
            var writer = new BlockWriter
            {
                chunkCoord = CaptureChunk,
                chunkSize = CaptureChunkSize,
                indexSize = CaptureChunkSize,
                blockIds = blockIds,
                pendingWrites = writes,
            };
            var rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            LSystemInterpreter.Interpret(in data, 0, 0, 0, scale, ref rng, ref writer);

            writeCount += writes.Length;
            int3 origin = CaptureChunk * CaptureChunkSize;
            var voxels = new Dictionary<int3, byte>(writes.Length);
            bool Solid(int3 q) => q.y <= GroundHeight(q.x, q.z, hilly) || voxels.ContainsKey(q);

            for (int i = 0; i < writes.Length; i++)
            {
                var w = writes[i];
                if (w.blockId == (byte)BlockType.Air)
                    continue;
                var p = w.targetChunk * CaptureChunkSize + w.localPos - origin;

                if (w.mode == PendingWriteMode.DropToSurface)
                {
                    bool found = false;
                    if (!Solid(p))
                    {
                        for (int n = 0; n < 16 && !found; n++, p.y--)
                            found = Solid(p + new int3(0, -1, 0));
                        p.y++;
                    }
                    else
                    {
                        for (int n = 0; n < 16 && !found; n++)
                        {
                            p.y++;
                            found = !Solid(p);
                        }
                    }
                    if (found)
                        voxels.TryAdd(p, w.blockId);
                    continue;
                }

                if (!Solid(p))
                    voxels.Add(p, w.blockId);
            }
            return voxels;
        }
        finally
        {
            writes.Dispose();
            blockIds.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Meshing
    // ------------------------------------------------------------------
    static readonly int3[] FaceDirs =
    {
        new int3(1, 0, 0), new int3(-1, 0, 0),
        new int3(0, 1, 0), new int3(0, -1, 0),
        new int3(0, 0, 1), new int3(0, 0, -1),
    };
    static readonly float[] FaceShade = { 0.80f, 0.70f, 1.00f, 0.50f, 0.88f, 0.62f };
    static readonly Vector2[] CornerUVs = { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };

    class MeshLists
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Color> Colors = new List<Color>();
        public readonly List<Vector4> UVs = new List<Vector4>(); // xy = uv, z = texture slice (-1 none), w = face shade
        public readonly List<int> Indices = new List<int>();

        public void Apply(ref Mesh mesh, string name)
        {
            if (mesh == null)
                mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.Clear();
            mesh.SetVertices(Vertices);
            mesh.SetColors(Colors);
            mesh.SetUVs(0, UVs);
            mesh.SetTriangles(Indices, 0);
            mesh.RecalculateBounds();
        }
    }

    void BuildMeshes(List<Dictionary<int3, byte>> captures, BlockType bodyBlock)
    {
        var lists = new MeshLists();
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

        bool any = false;
        var all = new Bounds();
        float offsetX = 0f;
        const float gap = 4f;
        groundPatches.Clear();

        foreach (var voxels in captures)
        {
            if (voxels.Count == 0)
                continue;

            int3 min = new int3(int.MaxValue), max = new int3(int.MinValue);
            foreach (var p in voxels.Keys)
            {
                min = math.min(min, p);
                max = math.max(max, p);
            }
            // Lay variants out left to right, each one's trunk on the ground line.
            var shift = new Vector3(offsetX - min.x, 0f, 0f);
            offsetX += (max.x - min.x + 1) + gap;

            foreach (var kv in voxels)
            {
                var block = (BlockType)kv.Value;
                if (block == bodyBlock) stats.BodyBlocks++;
                else stats.FoliageBlocks++;

                Color baseColor = ColorFor(block);
                int slice = SliceFor(block);
                uint h = math.hash(kv.Key);
                float jitter = 0.93f + (h & 0xFF) / 255f * 0.12f;

                for (int f = 0; f < 6; f++)
                {
                    if (voxels.ContainsKey(kv.Key + FaceDirs[f]))
                        continue;
                    float shade = FaceShade[f] * jitter;
                    var c = baseColor * shade;
                    c.a = 1f;
                    AddFace(lists, new Vector3(kv.Key.x, kv.Key.y, kv.Key.z) + shift, f, linear ? c.linear : c, slice, shade);
                }
            }
            stats.Blocks += voxels.Count;

            groundPatches.Add((shift, min, max));

            var b = new Bounds();
            b.SetMinMax(new Vector3(min.x, min.y, min.z) + shift, new Vector3(max.x + 1, max.y + 1, max.z + 1) + shift);
            if (any) all.Encapsulate(b); else all = b;
            any = true;

            stats.Size = Vector3Int.Max(stats.Size, new Vector3Int(max.x - min.x + 1, max.y - min.y + 1, max.z - min.z + 1));
        }

        if (!any)
            all = new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one);
        bounds = all;

        lists.Apply(ref treeMesh, "LSystemPreview");
        BuildGround(linear);
    }

    static void AddFace(MeshLists m, Vector3 p, int face, Color color, int slice, float shade)
    {
        int start = m.Vertices.Count;
        var v = m.Vertices;
        // Corners listed bottom-left, top-left, top-right, bottom-right as seen from outside
        // the cube: clockwise, which is Unity's front-face winding.
        switch (face)
        {
            case 0: v.Add(p + new Vector3(1, 0, 0)); v.Add(p + new Vector3(1, 1, 0)); v.Add(p + new Vector3(1, 1, 1)); v.Add(p + new Vector3(1, 0, 1)); break;
            case 1: v.Add(p + new Vector3(0, 0, 1)); v.Add(p + new Vector3(0, 1, 1)); v.Add(p + new Vector3(0, 1, 0)); v.Add(p + new Vector3(0, 0, 0)); break;
            case 2: v.Add(p + new Vector3(0, 1, 0)); v.Add(p + new Vector3(0, 1, 1)); v.Add(p + new Vector3(1, 1, 1)); v.Add(p + new Vector3(1, 1, 0)); break;
            case 3: v.Add(p + new Vector3(0, 0, 1)); v.Add(p + new Vector3(0, 0, 0)); v.Add(p + new Vector3(1, 0, 0)); v.Add(p + new Vector3(1, 0, 1)); break;
            case 4: v.Add(p + new Vector3(1, 0, 1)); v.Add(p + new Vector3(1, 1, 1)); v.Add(p + new Vector3(0, 1, 1)); v.Add(p + new Vector3(0, 0, 1)); break;
            default: v.Add(p + new Vector3(0, 0, 0)); v.Add(p + new Vector3(0, 1, 0)); v.Add(p + new Vector3(1, 1, 0)); v.Add(p + new Vector3(1, 0, 0)); break;
        }
        for (int i = 0; i < 4; i++)
        {
            m.Colors.Add(color);
            m.UVs.Add(new Vector4(CornerUVs[i].x, CornerUVs[i].y, slice, shade));
        }
        m.Indices.Add(start); m.Indices.Add(start + 1); m.Indices.Add(start + 2);
        m.Indices.Add(start); m.Indices.Add(start + 2); m.Indices.Add(start + 3);
    }

    // Block-sized checker of grass under each tree so scale is readable. It stands in for the
    // terrain: the tree spawns on the ground block at y = 0, so flat ground's top is at y = 1.
    void BuildGround(bool linear)
    {
        const int margin = 6;
        int slice = SliceFor(BlockType.Grass);
        Color grass = ColorFor(BlockType.Grass);
        var lists = new MeshLists();
        var sides = new[] { 0, 1, 4, 5 };

        foreach (var (shift, min, max) in groundPatches)
        {
            for (int x = min.x - margin; x <= max.x + margin; x++)
                for (int z = min.z - margin; z <= max.z + margin; z++)
                {
                    int h = GroundHeight(x, z, hillyGround);
                    float shade = ((x + z) & 1) == 0 ? 0.75f : 0.68f;
                    var c = grass * shade;
                    c.a = 1f;
                    AddFace(lists, new Vector3(x, h, z) + shift, 2, linear ? c.linear : c, slice, shade);

                    foreach (int f in sides)
                    {
                        int hn = GroundHeight(x + FaceDirs[f].x, z + FaceDirs[f].z, hillyGround);
                        for (int y = hn + 1; y <= h; y++)
                        {
                            float sideShade = shade * FaceShade[f];
                            var sc = grass * sideShade;
                            sc.a = 1f;
                            AddFace(lists, new Vector3(x, y, z) + shift, f, linear ? sc.linear : sc, slice, sideShade);
                        }
                    }
                }
        }
        lists.Apply(ref groundMesh, "LSystemPreviewGround");
    }

    // ------------------------------------------------------------------
    // Block colours + textures from the BlockDatabase
    // ------------------------------------------------------------------
    Color ColorFor(BlockType block)
    {
        if (palette != null && palette.TryGetValue(block, out var c))
            return c;

        switch (block)
        {
            case BlockType.Log: return new Color(0.45f, 0.32f, 0.2f);
            case BlockType.PineLeaves: return new Color(0.2f, 0.45f, 0.22f);
            case BlockType.RedLeaves: return new Color(0.7f, 0.2f, 0.18f);
            case BlockType.WillowLeaves: return new Color(0.4f, 0.6f, 0.3f);
            case BlockType.Grass: return new Color(0.35f, 0.55f, 0.25f);
            case BlockType.CherryBlossom: return new Color(0.92f, 0.6f, 0.73f);
            case BlockType.CherryBlossomPale: return new Color(0.98f, 0.85f, 0.9f);
            case BlockType.CherryBark: return new Color(0.33f, 0.2f, 0.19f);
            default:
                var hue = (math.hash(new int2((int)block, 7)) & 0xFF) / 255f;
                return Color.HSVToRGB(hue, 0.5f, 0.75f);
        }
    }

    int SliceFor(BlockType block) => slices != null && slices.TryGetValue(block, out int s) ? s : -1;

    void BuildBlockResources()
    {
        blockResourcesBuilt = true;
        palette = new Dictionary<BlockType, Color>();
        slices = new Dictionary<BlockType, int>();
        DestroyImmediate(blockTextures);

        BlockDatabase db = null;
        foreach (var guid in AssetDatabase.FindAssets("t:BlockDatabase"))
        {
            db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(AssetDatabase.GUIDToAssetPath(guid));
            if (db != null && db.blocks != null && db.blocks.Length > 0)
                break;
        }
        if (db == null || db.blocks == null || db.blocks.Length == 0)
            return;

        int size = 16;
        foreach (var entry in db.blocks)
            if (entry.texture != null)
                size = Mathf.Max(size, Mathf.Min(entry.texture.width, 128));

        blockTextures = new Texture2DArray(size, size, db.blocks.Length, TextureFormat.RGBA32, false, false)
        {
            name = "LSystemPreviewBlocks",
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat,
        };

        var blank = new Color[size * size];
        for (int i = 0; i < db.blocks.Length; i++)
        {
            var entry = db.blocks[i];
            if (entry.texture == null)
            {
                blockTextures.SetPixels(blank, i);
                continue;
            }

            var pixels = ReadPixels(entry.texture, size);
            blockTextures.SetPixels(pixels, i);
            slices.TryAdd(entry.type, i);
            palette.TryAdd(entry.type, Average(pixels));
        }
        blockTextures.Apply(false, true);
    }

    // Reads through a RenderTexture so it works whether or not the texture is CPU-readable.
    // The sRGB round trip keeps the values gamma-encoded, matching the sRGB texture array.
    static Color[] ReadPixels(Texture2D texture, int size)
    {
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.filterMode = FilterMode.Point;
        var previous = RenderTexture.active;
        var previousFilter = texture.filterMode;
        texture.filterMode = FilterMode.Point;
        Graphics.Blit(texture, rt);
        texture.filterMode = previousFilter;

        RenderTexture.active = rt;
        var readback = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
        readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        readback.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        var pixels = readback.GetPixels();
        DestroyImmediate(readback);
        return pixels;
    }

    static Color Average(Color[] pixels)
    {
        Color sum = Color.clear;
        float weight = 0f;
        foreach (var p in pixels)
        {
            if (p.a < 0.5f) continue;
            sum += p;
            weight++;
        }
        if (weight <= 0f)
            return Color.magenta;
        var avg = sum / weight;
        avg.a = 1f;
        return avg;
    }
}

// Adds an "Open Preview" button to the asset's regular inspector.
[CustomEditor(typeof(LSystemScriptableObject))]
public class LSystemScriptableObjectEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (GUILayout.Button("Open Preview Window", GUILayout.Height(24f)))
            LSystemPreviewWindow.Open((LSystemScriptableObject)target);
        EditorGUILayout.Space(4f);
        DrawDefaultInspector();
    }
}
