using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Distance based pixel art: voxels render in bands by distance from the camera, each band at
/// its own pixel size (square pixels of several screen pixels, one ray per pixel), with its own
/// anti-aliasing. A band can start at 0, pixelating the voxels right around the player. Voxels
/// nearer than the first band, and all objects, render at full resolution by the main camera.
///
/// Each band is a hidden camera that renders only the voxels in its distance range (its voxel
/// rays start at the band's distance and stop at the next band's, VoxelRaymarchCore.hlsl
/// _VoxelRayRange) into a small texture, drawing only its own voxel draws (on <see cref="Layer"/>).
/// Bands render far to near; each draws the band behind it as its background first
/// (CompositePass), so its water blends over what lies behind. The last band draws the sky.
///
/// The main camera then composites all bands, each on its own pixel grid (MainCompositePass): per
/// screen pixel, the nearest band that has something of its own there (a voxel or water surface
/// nearer than its end, from its depth). So a band's pixel size never leaks into the bands behind
/// it. The composite writes the band depth, so objects behind far terrain stay hidden. Fog: each
/// band fogs its own voxels, the main camera leaves the composite alone.
///
/// A band can snap its view to its own whole pixels (BandSettings.snap): rotation to its pixel
/// angle, sideways / vertical position to its pixel size at its distance. The composites shift it
/// by what the snap left out, so its image pans smoothly while its pixels hold still. Created and
/// updated by VoxelRenderSettings while playing.
/// </summary>
public sealed class VoxelFarLayer : IDisposable
{
    /// <summary>Layer of the band cameras' voxel draws: nothing else renders on it.</summary>
    public const int Layer = 31;
    public const int MaxBands = 4;   // VoxelFarComposite.shader

    public enum Filter { Nearest = 0, Sharp = 1 }
    public enum AntiAliasing { None, Fxaa, Smaa, Temporal }

    [Serializable]
    public struct BandSettings
    {
        [Tooltip("Distance from the camera (world units) where this band starts. 0 = right at the camera: no full resolution voxels.")]
        [Min(0f)] public float distance;
        [Tooltip("Screen pixels per pixel of this band, along each axis.")]
        [Range(1, 16)] public int pixelSize;
        [Tooltip("Anti-aliasing of this band's own image, at its pixel size. Temporal blends over frames (smooth, can ghost); FXAA / SMAA soften edges per frame.")]
        public AntiAliasing antiAliasing;
        [Tooltip("Upscale to the screen. Nearest: hard square pixels. Sharp: square pixels whose edges blend over one screen pixel.")]
        public Filter filter;
        [Tooltip("Average 2x2 rays per pixel of this band: calmer detail for 4x the rays.")]
        public bool supersample;
        [Tooltip("Snap this band's view to its own whole pixels and shift its image by the rest: its pixels hold still while the camera turns or strafes instead of crawling. (Moving forward still changes them.)")]
        public bool snap;
    }

    public static VoxelFarLayer Active { get; private set; }

    static readonly int RayRangeId = Shader.PropertyToID("_VoxelRayRange");
    static readonly int FogKeepBackgroundId = Shader.PropertyToID("_FogKeepBackground");
    static readonly int FarColorId = Shader.PropertyToID("_FarColor");
    static readonly int FarDepthId = Shader.PropertyToID("_FarDepth");
    static readonly int FarTargetId = Shader.PropertyToID("_FarTarget");
    static readonly int FarSourceId = Shader.PropertyToID("_FarSource");
    static readonly int FarModeId = Shader.PropertyToID("_FarMode");
    static readonly int FarCountId = Shader.PropertyToID("_FarCount");
    static readonly int FarSourcesId = Shader.PropertyToID("_FarSources");
    static readonly int FarModesId = Shader.PropertyToID("_FarModes");
    static readonly int FarOffsetsId = Shader.PropertyToID("_FarOffsets");
    static readonly int[] FarColorIds = { Shader.PropertyToID("_FarColor0"), Shader.PropertyToID("_FarColor1"), Shader.PropertyToID("_FarColor2"), Shader.PropertyToID("_FarColor3") };
    static readonly int[] FarDepthIds = { Shader.PropertyToID("_FarDepth0"), Shader.PropertyToID("_FarDepth1"), Shader.PropertyToID("_FarDepth2"), Shader.PropertyToID("_FarDepth3") };

    sealed class Band
    {
        public Camera cam;
        public UniversalAdditionalCameraData data;
        public BandSettings settings;
        public float start, end;   // end 0 = no end (last band)
        public int w, h;           // pixels of the band (the texture is twice that when supersampled)
        public bool ss;            // texture is supersampled
        public Vector2 offset;     // screen pixels: real view minus this band's snapped view
        public RenderTexture target;      // what the camera renders into (URP needs colour + depth)
        public RenderTexture color, depth; // colour copy and linear depth the composites read (render graph imports colour-only textures)
        public RTHandle colorHandle, depthHandle;
        public CompositePass composite;   // draws the next band behind this one (null for the last)
        public DepthCopyPass depthCopy;
        public int Pixel => Mathf.Max(1, settings.pixelSize);
    }

    public Camera Main { get; private set; }

    readonly Material material;
    readonly List<Band> bands = new();
    readonly MainCompositePass mainComposite;
    int margin;               // screen pixels each band texture reaches past the screen edges
    Vector2 screenSize;

    CameraClearFlags mainClearFlags;
    float savedScale;
    UpscalingFilterSelection savedFilter;
    bool overridingPipeline;

    public VoxelFarLayer(Camera main)
    {
        Main = main;
        material = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/ProGenWorld/VoxelFarComposite"));
        mainComposite = new MainCompositePass(this);

        mainClearFlags = main.clearFlags;
        main.clearFlags = CameraClearFlags.SolidColor;   // the bands are the background

        RenderPipelineManager.beginContextRendering += OnBeginContext;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
        Active = this;
    }

    /// <summary>Bands sorted by distance (at most <see cref="MaxBands"/>).</summary>
    public void Configure(BandSettings[] settings)
    {
        var sorted = new List<BandSettings>(settings ?? Array.Empty<BandSettings>());
        sorted.Sort((a, b) => a.distance.CompareTo(b.distance));
        if (sorted.Count > MaxBands)
            sorted.RemoveRange(MaxBands, sorted.Count - MaxBands);

        while (bands.Count > sorted.Count)
        {
            DestroyBand(bands[bands.Count - 1]);
            bands.RemoveAt(bands.Count - 1);
        }
        while (bands.Count < sorted.Count)
            bands.Add(CreateBand());

        margin = 1;
        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            var s = sorted[i];
            s.pixelSize = Mathf.Max(1, s.pixelSize);
            if (s.pixelSize != b.settings.pixelSize || s.supersample != b.ss)
                ReleaseTargets(b);
            b.settings = s;
            b.start = Mathf.Max(0f, s.distance);
            b.end = i + 1 < sorted.Count ? Mathf.Max(b.start, sorted[i + 1].distance) : 0f;
            margin = Mathf.Max(margin, b.Pixel);

            b.cam.clearFlags = i == bands.Count - 1 ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            b.data.antialiasing = s.antiAliasing switch
            {
                AntiAliasing.Fxaa => AntialiasingMode.FastApproximateAntialiasing,
                AntiAliasing.Smaa => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                AntiAliasing.Temporal => AntialiasingMode.TemporalAntiAliasing,
                _ => AntialiasingMode.None,
            };
            b.data.antialiasingQuality = AntialiasingQuality.High;
            // Anti-aliasing runs in URP's post processing; no volumes, so nothing else does (the
            // main camera post-processes the composite).
            b.data.renderPostProcessing = s.antiAliasing != AntiAliasing.None;
        }
    }

    Band CreateBand()
    {
        var go = new GameObject("Voxel Pixel Band Camera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = go.AddComponent<Camera>();
        cam.cullingMask = 1 << Layer;
        cam.allowMSAA = false;
        cam.allowHDR = true;
        cam.useOcclusionCulling = false;
        var data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;
        data.volumeLayerMask = 0;
        data.antialiasing = AntialiasingMode.None;
        data.renderShadows = false;          // objects are not drawn here; voxels shadow by rays
        data.requiresDepthTexture = true;
        var band = new Band { cam = cam, data = data };
        band.settings.pixelSize = -1;        // targets created on first use
        band.composite = new CompositePass(this);
        band.depthCopy = new DepthCopyPass(this, band);
        return band;
    }

    void DestroyBand(Band b)
    {
        ReleaseTargets(b);
        if (b.cam != null)
            UnityEngine.Object.Destroy(b.cam.gameObject);
    }

    public static bool IsBandCamera(Camera cam)
    {
        var layer = Active;
        if (layer != null)
            foreach (var b in layer.bands)
                if (cam == b.cam) return true;
        return false;
    }

    /// <summary>Radial distances a camera's voxel cells must overlap to be drawn (farthest &lt; 0: none).</summary>
    public static void CullRange(Camera cam, out float nearest, out float farthest)
    {
        nearest = 0f;
        farthest = float.MaxValue;
        var layer = Active;
        if (layer == null || layer.bands.Count == 0)
            return;
        if (cam == layer.Main)
        {
            farthest = layer.bands[0].start > 0f ? layer.bands[0].start : -1f;
            return;
        }
        foreach (var b in layer.bands)
        {
            if (cam != b.cam) continue;
            nearest = b.start;
            farthest = b.end > 0f ? b.end : float.MaxValue;
        }
    }

    /// <summary>Render layer for a camera's voxel draws.</summary>
    public static int DrawLayer(Camera cam, int defaultLayer) => IsBandCamera(cam) ? Layer : defaultLayer;

    // Once per frame before any camera: band cameras follow the main camera, each snapped to its own
    // pixels when set.
    void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras)
    {
        bool on = Main != null && Main.isActiveAndEnabled && bands.Count > 0;
        foreach (var b in bands)
            b.cam.enabled = on;
        if (!on)
            return;

        int screenW = Mathf.Max(1, Main.pixelWidth), screenH = Mathf.Max(1, Main.pixelHeight);
        screenSize = new Vector2(screenW, screenH);
        float focal = screenH * 0.5f / Mathf.Tan(Main.fieldOfView * 0.5f * Mathf.Deg2Rad);   // screen pixels per radian at the centre

        var proj = Matrix4x4.Perspective(Main.fieldOfView, screenW / (float)screenH, Main.nearClipPlane, Main.farClipPlane);
        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            int px = b.Pixel;
            int w = Mathf.CeilToInt((screenW + 2f * margin) / px);
            int h = Mathf.CeilToInt((screenH + 2f * margin) / px);
            EnsureTargets(b, w, h);

            b.cam.depth = Main.depth - 1f - i;   // far bands first
            SnappedView(b, focal, out var pos, out var rot);
            b.cam.transform.SetPositionAndRotation(pos, rot);
            b.cam.nearClipPlane = Main.nearClipPlane;
            b.cam.farClipPlane = Main.farClipPlane;
            b.cam.fieldOfView = Main.fieldOfView;

            // Main projection, widened from the screen to the band's whole pixels, starting
            // `margin` screen pixels left of / below the screen.
            float spanX = w * px, spanY = h * px;
            var widen = Matrix4x4.identity;
            widen.m00 = screenW / spanX; widen.m03 = (screenW + 2f * margin) / spanX - 1f;
            widen.m11 = screenH / spanY; widen.m13 = (screenH + 2f * margin) / spanY - 1f;
            b.cam.projectionMatrix = widen * proj;
        }
    }

    // The main camera's view, snapped to whole pixels of the band when it snaps: rotation to its
    // pixel angle, sideways / vertical position to its pixel size at its distance (at least a few
    // blocks, for a band that starts at the camera). offset: what the snap left out.
    void SnappedView(Band b, float focal, out Vector3 pos, out Quaternion rot)
    {
        pos = Main.transform.position;
        rot = Main.transform.rotation;
        b.offset = Vector2.zero;
        if (!b.settings.snap)
            return;

        int px = b.Pixel;
        float angleStep = px / focal * Mathf.Rad2Deg;
        Vector3 e = rot.eulerAngles;
        float yaw = Mathf.Round(e.y / angleStep) * angleStep;
        float pitch = Mathf.Round(e.x / angleStep) * angleStep;
        b.offset.x += Mathf.DeltaAngle(yaw, e.y) * Mathf.Deg2Rad * focal;
        b.offset.y -= Mathf.DeltaAngle(pitch, e.x) * Mathf.Deg2Rad * focal;
        rot = Quaternion.Euler(pitch, yaw, e.z);

        float grid = px * Mathf.Max(b.start, 8f) / focal;
        Vector3 right = rot * Vector3.right, up = rot * Vector3.up;
        float x = Vector3.Dot(pos, right), y = Vector3.Dot(pos, up);
        float sx = Mathf.Round(x / grid) * grid, sy = Mathf.Round(y / grid) * grid;
        pos += right * (sx - x) + up * (sy - y);
        b.offset += new Vector2(x - sx, y - sy) / grid * px;
    }

    void EnsureTargets(Band b, int w, int h)
    {
        if (b.color != null && b.w == w && b.h == h && b.ss == b.settings.supersample)
            return;
        ReleaseTargets(b);
        b.w = w;
        b.h = h;
        b.ss = b.settings.supersample;
        int ss = b.ss ? 2 : 1;

        // URP wants a camera's output texture to have depth; render graph imports only colour-only
        // textures, so the band renders into `target` and is copied into `color` (OnEndCamera).
        b.target = new RenderTexture(w * ss, h * ss, 24, GraphicsFormat.R16G16B16A16_SFloat)
        {
            name = "VoxelBandTarget", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        b.target.Create();
        b.color = new RenderTexture(w * ss, h * ss, 0, GraphicsFormat.R16G16B16A16_SFloat)
        {
            name = "VoxelBandColor", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        b.color.Create();
        b.depth = new RenderTexture(w, h, 0, GraphicsFormat.R32_SFloat)
        {
            name = "VoxelBandDepth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        b.depth.Create();
        b.colorHandle = RTHandles.Alloc(b.color);
        b.depthHandle = RTHandles.Alloc(b.depth);
        b.cam.targetTexture = b.target;
    }

    static void ReleaseTargets(Band b)
    {
        if (b.cam != null) b.cam.targetTexture = null;
        b.colorHandle?.Release(); b.colorHandle = null;
        b.depthHandle?.Release(); b.depthHandle = null;
        if (b.target != null) { b.target.Release(); UnityEngine.Object.Destroy(b.target); b.target = null; }
        if (b.color != null) { b.color.Release(); UnityEngine.Object.Destroy(b.color); b.color = null; }
        if (b.depth != null) { b.depth.Release(); UnityEngine.Object.Destroy(b.depth); b.depth = null; }
    }

    void OnBeginCamera(ScriptableRenderContext context, Camera cam)
    {
        // Globals are read when this camera's commands run: URP submits each camera before the next.
        if (cam == Main)
        {
            float near = bands.Count > 0 ? bands[0].start : 0f;
            Shader.SetGlobalVector(RayRangeId, new Vector4(0f, near, 0f, 0f));
            Shader.SetGlobalFloat(FogKeepBackgroundId, bands.Count > 0 ? 1f : 0f);
            if (bands.Count > 0)
                cam.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(mainComposite);
            return;
        }

        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            if (cam != b.cam)
                continue;

            // URP's render scale and STP would resample the band texture: render it 1:1.
            if (!overridingPipeline && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                savedScale = urp.renderScale;
                savedFilter = urp.upscalingFilter;
                urp.renderScale = 1f;
                if (savedFilter == UpscalingFilterSelection.STP || savedFilter == UpscalingFilterSelection.FSR)
                    urp.upscalingFilter = UpscalingFilterSelection.Linear;
                overridingPipeline = true;
            }

            bool last = i == bands.Count - 1;
            Shader.SetGlobalVector(RayRangeId, new Vector4(b.start, b.end, 0f, 0f));
            Shader.SetGlobalFloat(FogKeepBackgroundId, last ? 0f : 1f);
            var renderer = cam.GetUniversalAdditionalCameraData().scriptableRenderer;
            if (!last)
            {
                b.composite.Setup(b, bands[i + 1]);
                renderer.EnqueuePass(b.composite);
            }
            renderer.EnqueuePass(b.depthCopy);
            return;
        }

        Shader.SetGlobalVector(RayRangeId, Vector4.zero);
        Shader.SetGlobalFloat(FogKeepBackgroundId, 0f);
    }

    readonly CommandBuffer copyCmd = new() { name = "Voxel Band Copy" };

    void OnEndCamera(ScriptableRenderContext context, Camera cam)
    {
        if (!IsBandCamera(cam))
            return;

        // The finished band (fogged, anti-aliased) into the colour-only copy the composites read.
        foreach (var b in bands)
        {
            if (cam != b.cam || b.target == null)
                continue;
            copyCmd.Clear();
            copyCmd.Blit(b.target, b.color);
            context.ExecuteCommandBuffer(copyCmd);
            context.Submit();
        }

        if (!overridingPipeline)
            return;
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
        {
            urp.renderScale = savedScale;
            urp.upscalingFilter = savedFilter;
        }
        overridingPipeline = false;
    }

    public void Dispose()
    {
        RenderPipelineManager.beginContextRendering -= OnBeginContext;
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        if (Active == this)
            Active = null;
        Shader.SetGlobalVector(RayRangeId, Vector4.zero);
        Shader.SetGlobalFloat(FogKeepBackgroundId, 0f);

        if (Main != null)
            Main.clearFlags = mainClearFlags;
        foreach (var b in bands)
            DestroyBand(b);
        bands.Clear();
        copyCmd.Release();
        CoreUtils.Destroy(material);
    }

    // Source parameters of a band for the composite shader: x pixel size (screen pixels), y margin,
    // zw size (pixels).
    Vector4 SourceParams(Band b) => new(b.Pixel, margin, b.w, b.h);

    class PassData
    {
        public Material material;
        public TextureHandle color, depth, source;
        public Vector4 target, sourceParams, mode;
        public readonly TextureHandle[] colors = new TextureHandle[MaxBands], depths = new TextureHandle[MaxBands];
        public readonly Vector4[] sources = new Vector4[MaxBands], modes = new Vector4[MaxBands], offsets = new Vector4[MaxBands];
        public int count;
    }

    // Band camera: the band behind it as its background, with its depth, before its opaques
    // (nearest, on this band's pixels), so this band's water blends over it.
    sealed class CompositePass : ScriptableRenderPass
    {
        readonly VoxelFarLayer layer;
        Band target, source;

        public CompositePass(VoxelFarLayer layer)
        {
            this.layer = layer;
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            profilingSampler = new ProfilingSampler("Voxel Band Background");
        }

        public void Setup(Band target, Band source)
        {
            this.target = target;
            this.source = source;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (source == null || source.colorHandle == null)
                return;
            var resources = frameData.Get<UniversalResourceData>();
            float m = layer.margin;

            using var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Band Background", out var data, profilingSampler);
            data.material = layer.material;
            data.color = renderGraph.ImportTexture(source.colorHandle);
            data.depth = renderGraph.ImportTexture(source.depthHandle);
            // Target uv -> screen pixels of the source's view (the two bands may snap differently).
            Vector2 shift = source.offset - target.offset;
            data.target = new Vector4(target.w * target.Pixel, target.h * target.Pixel, -m + shift.x, -m + shift.y);
            data.sourceParams = layer.SourceParams(source);
            data.mode = new Vector4(0f, source.ss ? 1f : 0f, 0f, 0f);
            builder.UseTexture(data.color);
            builder.UseTexture(data.depth);
            builder.SetRenderAttachment(resources.activeColorTexture, 0);
            builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                d.material.SetTexture(FarColorId, d.color);
                d.material.SetTexture(FarDepthId, d.depth);
                d.material.SetVector(FarTargetId, d.target);
                d.material.SetVector(FarSourceId, d.sourceParams);
                d.material.SetVector(FarModeId, d.mode);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3);
            });
        }
    }

    // Main camera: all bands, each on its own pixel grid with its own filter, nearest band with
    // something of its own first, before the opaques.
    sealed class MainCompositePass : ScriptableRenderPass
    {
        readonly VoxelFarLayer layer;

        public MainCompositePass(VoxelFarLayer layer)
        {
            this.layer = layer;
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            profilingSampler = new ProfilingSampler("Voxel Pixel Bands");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var bands = layer.bands;
            if (bands.Count == 0 || bands[0].colorHandle == null)
                return;
            var resources = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Pixel Bands", out var data, profilingSampler);
            data.material = layer.material;
            data.target = new Vector4(layer.screenSize.x, layer.screenSize.y, 0f, 0f);
            data.count = 0;
            for (int i = 0; i < bands.Count && i < MaxBands; i++)
            {
                var b = bands[i];
                if (b.colorHandle == null)
                    break;
                data.colors[i] = renderGraph.ImportTexture(b.colorHandle);
                data.depths[i] = renderGraph.ImportTexture(b.depthHandle);
                builder.UseTexture(data.colors[i]);
                builder.UseTexture(data.depths[i]);
                data.sources[i] = layer.SourceParams(b);
                data.modes[i] = new Vector4((float)b.settings.filter, b.ss ? 1f : 0f, b.end, 0f);
                data.offsets[i] = b.offset;
                data.count = i + 1;
            }
            builder.SetRenderAttachment(resources.activeColorTexture, 0);
            builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < MaxBands; i++)
                {
                    var c = i < d.count ? d.colors[i] : d.colors[0];
                    var z = i < d.count ? d.depths[i] : d.depths[0];
                    d.material.SetTexture(FarColorIds[i], c);
                    d.material.SetTexture(FarDepthIds[i], z);
                }
                d.material.SetVector(FarTargetId, d.target);
                d.material.SetVectorArray(FarSourcesId, d.sources);
                d.material.SetVectorArray(FarModesId, d.modes);
                d.material.SetVectorArray(FarOffsetsId, d.offsets);
                d.material.SetInt(FarCountId, d.count);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 1, MeshTopology.Triangles, 3);
            });
        }
    }

    // A band's depth (its voxels and water, and the bands behind it), as linear eye depth
    // (0 = sky), one value per band pixel, for the composites.
    sealed class DepthCopyPass : ScriptableRenderPass
    {
        readonly VoxelFarLayer layer;
        readonly Band band;

        public DepthCopyPass(VoxelFarLayer layer, Band band)
        {
            this.layer = layer;
            this.band = band;
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            profilingSampler = new ProfilingSampler("Voxel Band Depth");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (band.depthHandle == null)
                return;
            var resources = frameData.Get<UniversalResourceData>();
            if (!resources.activeDepthTexture.IsValid())
                return;

            using var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Band Depth", out var data, profilingSampler);
            data.material = layer.material;
            data.source = resources.activeDepthTexture;   // includes water and the background's depth
            builder.UseTexture(data.source);
            builder.SetRenderAttachment(renderGraph.ImportTexture(band.depthHandle), 0);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 2);
            });
        }
    }
}
