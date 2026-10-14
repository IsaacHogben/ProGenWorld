// RenderBenchmark.cs
// Command-line driven GPU benchmark for the voxel renderer in a standalone build.
// Inactive unless the player is started with -benchRender.
//
// After the initial load it holds the camera at a few fixed views around the player and, for
// each view, measures every configuration (a baseline plus one feature toggled at a time).
// Results are written as JSON; -benchShots also saves a screenshot per view and configuration.
//
// Args: -benchRender  -benchOut <file.json>  -benchShotDir <dir>  -benchShots <config,config|all>
//       -benchConfigs <config,config>  -benchViews <view,view>  -benchFrames <n>

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(-10000)]
public class RenderBenchmark : MonoBehaviour
{
    struct View
    {
        public string name;
        public Vector3 offset;   // camera position relative to the player
        public float yaw, pitch;
        public bool lookAtPlayer;
        public bool overWater;   // offset / yaw relative to the nearest open water instead of the player
        public bool playerInWater;   // move the player into shallow water first (lookAtPlayer views)
    }

    static readonly View[] Views =
    {
        new View { name = "thirdPerson", offset = new Vector3(0f, 3.5f, -6f), lookAtPlayer = true },
        new View { name = "horizon", offset = new Vector3(0f, 2f, 0f), yaw = 30f, pitch = 4f },
        new View { name = "overlook", offset = new Vector3(0f, 40f, 0f), yaw = 140f, pitch = 30f },
        new View { name = "farOverlook", offset = new Vector3(0f, 160f, 0f), yaw = 140f, pitch = 10f },
        new View { name = "skyHigh", offset = new Vector3(0f, 700f, 0f), yaw = 140f, pitch = 12f },
        new View { name = "ground", offset = new Vector3(0f, 8f, 0f), yaw = 250f, pitch = 60f },
        new View { name = "waterLow", offset = new Vector3(0f, 2.5f, 0f), pitch = 8f, overWater = true },
        new View { name = "waterHigh", offset = new Vector3(0f, 14f, 0f), pitch = 35f, overWater = true },
        new View { name = "waterFar", offset = new Vector3(0f, 30f, 0f), pitch = 28f, overWater = true },
        new View { name = "underwater", offset = new Vector3(0f, -0.6f, 0f), pitch = -30f, overWater = true },
        new View { name = "wading", offset = new Vector3(2.5f, 1.8f, -4f), lookAtPlayer = true, playerInWater = true },
    };

    // Open water near the player (top of a water surface block) and the direction with the most
    // water ahead of it, found once after loading. Water views are skipped when there is none.
    Vector3? waterSpot;
    Vector3? shallowSpot;   // water one block deep, for the player to stand in
    float waterYaw;

    string outPath, shotDir;
    int frames = 150;
    int stabilityFrames;   // consecutive screenshots per shot, to see frame to frame flicker
    HashSet<string> shotConfigs = new();
    bool shotAll;
    List<string> configNames;
    HashSet<string> viewFilter;   // -benchViews: only these views

    ChunkManager chunkManager;
    VoxelRenderSettings settings;
    UniversalRenderPipelineAsset urp;
    Camera cam;
    Transform player;
    bool loadComplete;

    // Baseline values, restored before each configuration.
    readonly Dictionary<FieldInfo, object> baselineFields = new();
    float baseRenderScale;
    UpscalingFilterSelection baseUpscaler;
    int baseMsaa;
    readonly List<ScriptableRendererFeature> ssaoFeatures = new();

    readonly Dictionary<string, Action> configs = new();
    readonly StringBuilder json = new();
    readonly List<string> log = new();

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == name) return i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : "";
        return null;
    }

    void Awake()
    {
        if (Arg("-benchRender") == null)
        {
            Destroy(this);
            return;
        }

        outPath = Arg("-benchOut");
        if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Application.persistentDataPath, "renderbench.json");
        shotDir = Arg("-benchShotDir");
        if (int.TryParse(Arg("-benchFrames"), out int f)) frames = f;
        int.TryParse(Arg("-benchStability"), out stabilityFrames);
        string shots = Arg("-benchShots");
        if (!string.IsNullOrEmpty(shots))
        {
            shotAll = shots == "all";
            foreach (var s in shots.Split(',')) shotConfigs.Add(s.Trim());
        }

        chunkManager = FindFirstObjectByType<ChunkManager>();
        settings = FindFirstObjectByType<VoxelRenderSettings>();
        urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        player = chunkManager.player.transform;

        // World settings that need a restart to change (applied before the chunk manager starts)
        var view = FindFirstObjectByType<ViewDistanceSettings>();
        if (view != null)
        {
            if (int.TryParse(Arg("-benchView"), out int viewDistance)) view.viewDistance = viewDistance;
            if (int.TryParse(Arg("-benchTrimGpu"), out int trim)) view.trimGpuDetail = trim != 0;
            if (int.TryParse(Arg("-benchHorizon"), out int horizon)) view.lowResHorizon = horizon != 0;
            if (int.TryParse(Arg("-benchFarHorizon"), out int farHorizon)) view.farHorizon = farHorizon != 0;
            Log($"view distance {view.viewDistance}, trim gpu detail {view.trimGpuDetail}, lowest detail range {view.lowestDetailRange}, low res horizon {view.lowResHorizon}");
        }

        foreach (var p in FindObjectsByType<ChunkProfiler>(FindObjectsSortMode.None))
            p.enabled = false;
        foreach (var pc in FindObjectsByType<PlayerCamera>(FindObjectsSortMode.None))
            pc.enabled = false;

        DefineConfigs();
        string views = Arg("-benchViews");
        if (!string.IsNullOrEmpty(views)) viewFilter = new HashSet<string>(views.Split(','));
        string only = Arg("-benchConfigs");
        configNames = new List<string>(string.IsNullOrEmpty(only) ? configs.Keys : only.Split(','));

        Application.logMessageReceived += OnLog;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
    }

    void OnDestroy() => Application.logMessageReceived -= OnLog;

    void OnLog(string msg, string stack, LogType type)
    {
        if (!loadComplete && msg.StartsWith("Initial Loading Complete"))
            loadComplete = true;
        if (type == LogType.Exception || type == LogType.Error)
            Log($"[{type}] {msg}");
    }

    void Log(string s)
    {
        log.Add(s);
        Debug.Log("[RenderBench] " + s);
    }

    void DefineConfigs()
    {
        configs["baseline"] = () => { };
        configs["noVoxelShadows"] = () => settings.shadowMaxDistance = 0f;
        configs["coarseShadows"] = () => settings.shadowDetailDistance = 0f;
        configs["shadowFull"] = () => settings.shadowResolution = VoxelRenderSettings.ShadowResolution.Full;
        configs["shadowQuarter"] = () => settings.shadowResolution = VoxelRenderSettings.ShadowResolution.Quarter;
        configs["smoothShadows"] = () => settings.pixelatedShadows = false;
        configs["noReuse"] = () => settings.reusePrepass = false;
        configs["noCornerAO"] = () => settings.ambientOcclusion = 0f;
        configs["noSSAO"] = () => { foreach (var s in ssaoFeatures) s.SetActive(false); };
        configs["relief"] = () => settings.relief = true;
        configs["noGrass"] = () => settings.grass = false;
        configs["noGrassShadows"] = () => settings.grassShadows = false;
        configs["flatPatches"] = () => settings.grassPatchHeight = 0f;
        configs["grass64"] = () => { settings.grassDistance = 64f; settings.grassShadows = false; };
        configs["noFarShade"] = () => { settings.grassFarShade = 0f; settings.grassShadows = false; settings.grassDistance = 64f; };
        configs["noWind"] = () => settings.windStrength = 0f;
        configs["noLeafFlutter"] = () => settings.leafFlutter = 0f;
        configs["noGrassSway"] = () => settings.grassSway = 0f;
        configs["grass1Blade"] = () => { settings.grassBladesPerBlock = 1f; settings.grassShadows = false; };
        configs["grass8Blades"] = () => { settings.grassBladesPerBlock = 8f; settings.grassShadows = false; };
        configs["native"] = () => settings.renderScale = 1f;
        configs["scale67"] = () => settings.renderScale = 0.67f;
        configs["scale50"] = () => settings.renderScale = 0.5f;
        configs["taa"] = () => settings.antiAliasing = VoxelRenderSettings.AntiAliasing.Temporal;
        configs["taaNative"] = () => { settings.antiAliasing = VoxelRenderSettings.AntiAliasing.Temporal; settings.renderScale = 1f; };
        configs["noFog"] = () => { foreach (var fc in FindObjectsByType<AtmosphericFogController>(FindObjectsSortMode.None)) fc.enabled = false; };
        configs["fxaa"] = () => settings.antiAliasing = VoxelRenderSettings.AntiAliasing.Fxaa;
        configs["fxaaNative"] = () => { settings.antiAliasing = VoxelRenderSettings.AntiAliasing.Fxaa; settings.renderScale = 1f; };
        configs["noReflections"] = () => settings.reflections = VoxelRenderSettings.WaterReflections.Off;
        configs["skyReflections"] = () => settings.reflections = VoxelRenderSettings.WaterReflections.Sky;
        configs["ssr32"] = () => settings.reflectionSteps = 32;
        configs["calm"] = () => settings.windDirection = Vector2.zero;
        configs["strongPixels"] = () => { settings.pixelNoise = 0.8f; settings.surfaceOpacity = 0.5f; };
        configs["opacity45"] = () => settings.surfaceOpacity = 0.45f;
        configs["opacity65"] = () => settings.surfaceOpacity = 0.65f;
        configs["noFoam"] = () => settings.foamWidth = 0f;
        configs["noAbsorption"] = () => { settings.absorption = 0f; settings.underwaterAbsorption = 0f; };
        configs["noSurface"] = () => settings.surfaceOpacity = 0f;
        configs["plainWater"] = () =>   // the water look before textures / ripples / reflections
        {
            settings.reflections = VoxelRenderSettings.WaterReflections.Off;
            settings.pixelNoise = 0f;
            settings.windDirection = Vector2.zero;
        };
        configs["noFarHorizon"] = () => FindFirstObjectByType<ViewDistanceSettings>().farHorizon = false;
        configs["noLowestDetail"] = () => FindFirstObjectByType<ViewDistanceSettings>().lowestDetailRange = 0;
        configs["baselineAgain"] = () => { };
    }

    void CaptureBaseline()
    {
        foreach (var fi in typeof(VoxelRenderSettings).GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!typeof(UnityEngine.Object).IsAssignableFrom(fi.FieldType))
                baselineFields[fi] = fi.GetValue(settings);
        baseRenderScale = urp.renderScale;
        baseUpscaler = urp.upscalingFilter;
        baseMsaa = urp.msaaSampleCount;

        // SSAO lives on the renderer; its feature list is protected.
        var renderer = urp.GetRenderer(0);
        var prop = typeof(ScriptableRenderer).GetProperty("rendererFeatures", BindingFlags.Instance | BindingFlags.NonPublic);
        if (prop?.GetValue(renderer) is List<ScriptableRendererFeature> features)
            foreach (var feat in features)
                if (feat != null && feat.GetType().Name.Contains("AmbientOcclusion") && feat.isActive)
                    ssaoFeatures.Add(feat);
        Log($"baseline: renderScale {baseRenderScale} upscaler {baseUpscaler} msaa {baseMsaa} ssao {ssaoFeatures.Count}");
    }

    void RestoreBaseline()
    {
        foreach (var kv in baselineFields) kv.Key.SetValue(settings, kv.Value);
        urp.msaaSampleCount = baseMsaa;
        foreach (var fc in FindObjectsByType<AtmosphericFogController>(FindObjectsSortMode.None)) fc.enabled = true;
        foreach (var s in ssaoFeatures) s.SetActive(true);
    }

    void PlaceCamera(View v)
    {
        if (v.overWater)
        {
            // Back off so the view centre lands on the far side of the water spot.
            Vector3 flat = Quaternion.Euler(0f, waterYaw, 0f) * Vector3.forward;
            float back = v.offset.y > 0f ? 0.5f * v.offset.y / Mathf.Tan(Mathf.Max(v.pitch, 3f) * Mathf.Deg2Rad) : 0f;
            cam.transform.position = waterSpot.Value + v.offset - flat * back;
            cam.transform.rotation = Quaternion.Euler(v.pitch, waterYaw, 0f);
            return;
        }
        Vector3 p = player.position;
        cam.transform.position = p + v.offset;
        cam.transform.rotation = v.lookAtPlayer
            ? Quaternion.LookRotation(p + Vector3.up * 1.2f - cam.transform.position)
            : Quaternion.Euler(v.pitch, v.yaw, 0f);
    }

    IEnumerator Start()
    {
        float start = Time.realtimeSinceStartup;
        while (!loadComplete && Time.realtimeSinceStartup - start < 300f)
            yield return null;
        Log($"load {(loadComplete ? "complete" : "TIMED OUT")} in {Time.realtimeSinceStartup - start:F1}s");

        // Let decoration and LOD streaming settle, then freeze the world as far as possible.
        yield return Wait(8f);

        // Far horizon tiles build one at a time after the chunks
        float horizonStart = Time.realtimeSinceStartup;
        while (chunkManager.Horizon != null && (chunkManager.Horizon.Busy || chunkManager.Horizon.PendingTiles > 0)
               && Time.realtimeSinceStartup - horizonStart < 120f)
            yield return null;
        if (chunkManager.Horizon != null)
            Log($"far horizon: {chunkManager.Horizon.TileCount} tiles, {chunkManager.VoxelRenderer.HorizonChunkCount} chunks, built in {Time.realtimeSinceStartup - horizonStart:F1}s");
        cam = Camera.main;
        CaptureBaseline();
        Log($"screen {Screen.width}x{Screen.height}, player at {player.position}");
        playerStart = player.position;
        FindWater();

        json.Append("{\n  \"results\": [\n");
        bool first = true;
        foreach (var view in Views)
        {
            if ((view.overWater && waterSpot == null) || (view.playerInWater && shallowSpot == null))
                continue;
            if (viewFilter != null && !viewFilter.Contains(view.name))
                continue;
            if (view.playerInWater)
                player.GetComponent<VoxelCharacterMotor>().Teleport(shallowSpot.Value);
            PlaceCamera(view);
            yield return Wait(3f);   // stream in whatever the view needs

            foreach (var name in configNames)
            {
                if (!configs.TryGetValue(name, out var apply))
                {
                    Log($"unknown config {name}");
                    continue;
                }
                RestoreBaseline();
                apply();
                settings.Apply();
                PlaceCamera(view);

                for (int i = 0; i < 40; i++) yield return null;   // warm up (TAA history, shader variants)

                var stats = new FrameStats();
                for (int i = 0; i < frames; i++)
                {
                    PlaceCamera(view);
                    yield return null;
                    stats.Add();
                }

                if (shotDir != null && (shotAll || shotConfigs.Contains(name)))
                {
                    Directory.CreateDirectory(shotDir);
                    if (stabilityFrames > 0)
                    {
                        for (int n = 0; n < stabilityFrames; n++)
                        {
                            ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, $"{view.name}_{name}_f{n}.png"));
                            yield return null;
                        }
                    }
                    else
                        ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, $"{view.name}_{name}.png"));
                    yield return null;
                    yield return null;
                }

                json.Append(first ? "" : ",\n");
                first = false;
                json.Append($"    {{ \"view\": \"{view.name}\", \"config\": \"{name}\", {stats.ToJson()} }}");
                Log($"{view.name,-12} {name,-16} gpu {stats.GpuMean:F2} ms  cpu {stats.CpuMean:F2} ms");
            }
        }
        RestoreBaseline();
        if (playerStart != null)
            player.GetComponent<VoxelCharacterMotor>().Teleport(playerStart.Value);

        if (shotDir != null)
            yield return ShadowProbe();

        json.Append("\n  ],\n  \"log\": [");
        for (int i = 0; i < log.Count; i++)
            json.Append((i > 0 ? ", " : "") + "\"" + log[i].Replace("\\", "/").Replace("\"", "'") + "\"");
        json.Append("]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        File.WriteAllText(outPath, json.ToString());
        Debug.Log($"[RenderBench] Wrote {outPath}");
        Application.Quit();
    }

    // Top of the first non-air block in a column, when that block is water.
    static bool WaterSurface(int x, int z, int top, int bottom, out int y)
    {
        for (y = top; y >= bottom; y--)
        {
            // Empty sky chunks are not stored: unloaded counts as air.
            if (VoxelPhysics.TryGetBlock(new Unity.Mathematics.int3(x, y, z), out byte id) && id != (byte)BlockType.Air)
                return id == (byte)BlockType.Water;
        }
        return false;
    }

    Vector3? playerStart;

    void FindWater()
    {
        Vector3 p = player.position;
        int top = Mathf.FloorToInt(p.y) + 40, bottom = Mathf.FloorToInt(p.y) - 120;
        float best = float.MaxValue;
        for (int dz = -256; dz <= 256; dz += 4)
        for (int dx = -256; dx <= 256; dx += 4)
        {
            int x = Mathf.FloorToInt(p.x) + dx, z = Mathf.FloorToInt(p.z) + dz;
            if (!WaterSurface(x, z, top, bottom, out int y))
                continue;
            // Wide water only: the same surface 12 blocks away on all four sides.
            if (!WaterSurface(x + 12, z, y + 1, y, out _) || !WaterSurface(x - 12, z, y + 1, y, out _)
                || !WaterSurface(x, z + 12, y + 1, y, out _) || !WaterSurface(x, z - 12, y + 1, y, out _))
                continue;
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; waterSpot = new Vector3(x + 0.5f, y + 1f, z + 0.5f); }
        }
        if (waterSpot == null)
        {
            Log("no open water near the player: water views skipped");
            return;
        }

        // Look towards the shore that is farthest away, across the most water.
        int maxRun = -1;
        for (int a = 0; a < 360; a += 30)
        {
            Vector3 dir = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
            int run = 0;
            Vector3 w = waterSpot.Value;
            while (run < 96 && WaterSurface(Mathf.FloorToInt(w.x + dir.x * run), Mathf.FloorToInt(w.z + dir.z * run), (int)w.y, (int)w.y - 1, out _))
                run++;
            if (run > maxRun) { maxRun = run; waterYaw = a; }
        }
        Log($"water at {waterSpot.Value}, looking {waterYaw} deg across {maxRun} blocks");

        // Shallow water near the open water, for the player to stand in: one water block on solid ground.
        float bestShallow = float.MaxValue;
        Vector3 ws = waterSpot.Value;
        int wy = Mathf.FloorToInt(ws.y) - 1;
        for (int dz = -48; dz <= 48; dz++)
        for (int dx = -48; dx <= 48; dx++)
        {
            int x = Mathf.FloorToInt(ws.x) + dx, z = Mathf.FloorToInt(ws.z) + dz;
            if (!WaterSurface(x, z, wy + 1, wy, out int y) || VoxelPhysics.IsWater(new Unity.Mathematics.int3(x, y - 1, z))
                || !VoxelPhysics.IsSolid(new Unity.Mathematics.int3(x, y - 1, z)))
                continue;
            float d = dx * dx + dz * dz;
            if (d < bestShallow) { bestShallow = d; shallowSpot = new Vector3(x + 0.5f, y, z + 0.5f); }
        }
        Log(shallowSpot != null ? $"shallow water at {shallowSpot.Value}" : "no shallow water: wading view skipped");
    }

    // Terrain shadows on objects: a cube with the player's (URP Lit) material in a spot the terrain
    // shades from the sun. It should be dark: it gets the voxel shadow rays through URP's screen
    // space shadows, and it casts its own shadow onto the terrain through the shadow map.
    IEnumerator ShadowProbe()
    {
        Vector3 toSun = -MainLightForward();
        Vector3 p = player.position;
        Vector3 best = Vector3.zero, lit = Vector3.zero;
        float bestDist = float.MaxValue, litDist = float.MaxValue;
        for (int dz = -24; dz <= 24; dz++)
        for (int dx = -24; dx <= 24; dx++)
        {
            int x = Mathf.FloorToInt(p.x) + dx, z = Mathf.FloorToInt(p.z) + dz;
            if (!VoxelPhysics.TryGetColumnSurface(x, z, p.y + 30f, p.y - 30f, out float y))
                continue;
            var c = new Vector3(x + 0.5f, y + 0.3f, z + 0.5f);
            float d = (c - p).sqrMagnitude;
            // Occluder at least 2 blocks away, so the shadow map bias does not hide it.
            bool shaded = VoxelPhysics.Raycast(c, toSun, out var hit, 64f) && hit.distance > 2f
                       && VoxelPhysics.Raycast(c + Vector3.right * 0.25f, toSun, 64f)
                       && VoxelPhysics.Raycast(c - Vector3.right * 0.25f, toSun, 64f);
            if (shaded && d < bestDist) { bestDist = d; best = c; }
            if (!VoxelPhysics.Raycast(c, toSun, 64f) && d < litDist) { litDist = d; lit = c; }
        }
        if (bestDist == float.MaxValue)
        {
            Log("shadow probe: no shaded spot found");
            yield break;
        }

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(cube.GetComponent<Collider>());
        cube.transform.localScale = Vector3.one * 0.6f;
        cube.transform.position = best;
        // The player model's own material: it is the receiver that has to pick up terrain shadows.
        var playerRenderer = player.GetComponentInChildren<Renderer>();
        if (playerRenderer != null) cube.GetComponent<Renderer>().sharedMaterial = playerRenderer.sharedMaterial;
        Log($"shadow probe at {best} (lit reference {lit}), shader {cube.GetComponent<Renderer>().sharedMaterial?.shader?.name}");

        // Look down at the cube from the side away from the sun's azimuth.
        Vector3 away = Vector3.ProjectOnPlane(-toSun, Vector3.up).normalized;
        cam.transform.position = best + away * 3f + Vector3.up * 3f;
        cam.transform.rotation = Quaternion.LookRotation(best - cam.transform.position);

        Directory.CreateDirectory(shotDir);
        RestoreBaseline();
        for (int i = 0; i < 60; i++) yield return null;
        ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, "probe.png"));
        yield return null;
        yield return null;
        RestoreBaseline();
        Destroy(cube);
    }

    static Vector3 MainLightForward()
    {
        var sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.isActiveAndEnabled) { sun = l; break; }
        return sun != null ? sun.transform.forward : Vector3.down;
    }

    static IEnumerator Wait(float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end) yield return null;
    }

    static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    class FrameStats
    {
        readonly List<float> cpu = new(), gpu = new(), mainThread = new();
        readonly FrameTiming[] timings = new FrameTiming[1];

        public float GpuMean => Mean(gpu);
        public float CpuMean => Mean(cpu);

        public void Add()
        {
            cpu.Add(Time.unscaledDeltaTime * 1000f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                if (timings[0].gpuFrameTime > 0) gpu.Add((float)timings[0].gpuFrameTime);
                if (timings[0].cpuMainThreadFrameTime > 0) mainThread.Add((float)timings[0].cpuMainThreadFrameTime);
            }
        }

        static float Mean(List<float> l)
        {
            if (l.Count == 0) return 0f;
            double s = 0;
            foreach (var v in l) s += v;
            return (float)(s / l.Count);
        }

        static float P50(List<float> l)
        {
            if (l.Count == 0) return 0f;
            var s = new List<float>(l);
            s.Sort();
            return s[s.Count / 2];
        }

        public string ToJson() =>
            $"\"frames\": {cpu.Count}, \"gpuMean\": {F(Mean(gpu))}, \"gpuP50\": {F(P50(gpu))}, " +
            $"\"cpuMean\": {F(Mean(cpu))}, \"mainThreadMean\": {F(Mean(mainThread))}";
    }
}
