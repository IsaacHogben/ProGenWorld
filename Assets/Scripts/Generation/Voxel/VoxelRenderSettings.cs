using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Scene settings for the raymarched voxel renderer. Put one on its own GameObject
/// (GameObject > ProGenWorld > Voxel Render Settings). Look values are pushed to the shaders
/// as globals and update live, also while playing.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class VoxelRenderSettings : MonoBehaviour
{
    public enum TextureBlendMode { Lerp = 0, Multiply = 1 }
    public enum AntiAliasing { Camera, None, Fxaa, Smaa, Temporal }
    public enum ShadowResolution { Full = 1, Half = 2, Quarter = 4 }
    public enum FarShadowDetail { Half = 1, Quarter = 2, Bricks = 3 }

    [Header("Materials")]
    [Tooltip("Raymarching material for opaque + alpha masked blocks. Defaults to Resources/VoxelRaymarchOpaque.")]
    public Material opaqueMaterial;
    [Tooltip("Raymarching material for translucent blocks (water). Defaults to Resources/VoxelRaymarchTranslucent.")]
    public Material translucentMaterial;

    [Header("Texture Variation")]
    [Tooltip("Blocks covered by one repeat of the detail texture.")]
    [Min(0.01f)] public float smallScale = 2f;
    [Tooltip("Blocks covered by one repeat of the large variation texture.")]
    [Min(0.01f)] public float largeScale = 32f;
    [Tooltip("How much of the large scale texture is mixed in.")]
    [Range(0f, 1f)] public float largeBlend = 0.35f;
    [Tooltip("Lerp: mix the two samples. Multiply: the large sample brightens/darkens the small one.")]
    public TextureBlendMode blendMode = TextureBlendMode.Lerp;

    [Header("Surface")]
    [Range(0f, 1f)] public float smoothness = 0.1f;
    [Tooltip("Darkens ambient light in the corners and along the edges where blocks meet (per block corner, interpolated across faces). 0 = off.")]
    [Range(0f, 1f)] public float ambientOcclusion = 0.8f;

    [Header("Anti-Aliasing & Resolution")]
    [Tooltip("Anti-aliasing set on the camera while playing. MSAA does not help raymarched voxels (only the box outlines are rasterized), so TAA is the one that smooths block edges, thin leaves and distant detail. TAA turns MSAA off on the camera, it does not work with it. Camera = leave the camera and the render scale as they are.")]
    public AntiAliasing antiAliasing = AntiAliasing.Temporal;
    [Tooltip("Resolution the scene renders at, relative to the screen (raymarching cost scales with the pixel count). Upscaled with STP when Anti-Aliasing is Temporal, bilinear otherwise. Applied to the URP asset while playing and restored afterwards.")]
    [Range(0.5f, 1f)] public float renderScale = 0.75f;
    [Tooltip("Camera the anti-aliasing is applied to. Empty = Camera.main.")]
    public Camera targetCamera;

    [Header("Performance")]
    [Tooltip("Shade from URP's depth + normals prepass, which already marched every voxel pixel, instead of marching each ray a second time in the forward pass.")]
    public bool reusePrepass = true;

    [Header("Level Of Detail")]
    [Tooltip("Leaves and other alpha masked blocks render solid from this detail level on (1 = half resolution, 2 = quarter resolution). Rays stop at the first leaf instead of marching through the canopy; far away the mask holes are smaller than a pixel anyway. 0 = never.")]
    [Range(0, 2)] public int solidLeavesFromLod = 1;

    [Header("Texel Relief")]
    [Tooltip("Carve nearby block faces into small columns, one per texture texel: dark texels sink, bright texels stay at the surface. Follows the Small Scale texel grid.")]
    public bool relief = true;
    [Tooltip("Deepest carve (blocks), reached by a texture's darkest texel. Rounded to whole texels.")]
    [Range(0f, 0.5f)] public float reliefDepth = 0.1875f;
    [Tooltip("Thickness (blocks) of alpha masked faces (leaves): the opaque texels become small cubes, visible through the holes. 0 = flat cards. Rounded to whole texels.")]
    [Range(0f, 0.5f)] public float leafThickness = 0.125f;
    [Tooltip("Surfaces closer to the camera than this (world units) get relief.")]
    [Min(0f)] public float reliefDistance = 24f;
    [Tooltip("Distance over which the relief flattens out before Relief Distance.")]
    [Min(0f)] public float reliefFade = 8f;
    [Tooltip("Darkens ambient light at the bottom of the deepest texels.")]
    [Range(0f, 1f)] public float reliefOcclusion = 0.4f;
    [Tooltip("Relief columns cast sun shadows on each other.")]
    public bool reliefShadows = true;

    [Header("Grass")]
    [Tooltip("Grow thin pixel art grass blades on the top face of blocks whose Surface Decoration is Grass (Block Database), when the block above is air. Blades take the colour of the top texel they stand on.")]
    public bool grass = true;
    [Tooltip("Tallest blade (blocks). Rounded to whole texels; blades vary between about half of this and all of it.")]
    [Range(0f, 0.9375f)] public float grassHeight = 0.5f;
    [Tooltip("Blades on a block that shows grass, on average.")]
    [Range(0f, 16f)] public float grassBladesPerBlock = 5f;
    [Tooltip("Share of grass blocks that show grass. Bare and grassy blocks form patches a few blocks wide.")]
    [Range(0f, 1f)] public float grassCoverage = 0.6f;
    [Tooltip("Widest blade root (texels). Blades narrow to one texel at the tip.")]
    [Range(1, 4)] public int grassWidth = 2;
    [Tooltip("Most a blade's tip leans sideways (texels).")]
    [Range(0, 4)] public int grassLean = 2;
    [Tooltip("Blades rise towards the densest centre of a coverage patch, where they reach Grass Height: 1 = blades at the patch edges are half as tall as at the centre. 0 = same height everywhere.")]
    [Range(0f, 2f)] public float grassPatchHeight = 0.75f;
    [Tooltip("Blades cast sun shadows on the ground and on each other.")]
    public bool grassShadows = true;
    [Tooltip("Darkens ambient light towards the blade roots.")]
    [Range(0f, 1f)] public float grassRootShade = 0.4f;
    [Tooltip("Grass closer to the camera than this (world units) has blades.")]
    [Min(0f)] public float grassDistance = 32f;
    [Tooltip("Distance over which the blades shrink into the ground before Grass Distance.")]
    [Min(0f)] public float grassFade = 10f;
    [Tooltip("Past Grass Distance, grass tops are darkened where their patch would grow blades, so patches still show far away and the blade cutoff blends in. 0 = off.")]
    [Range(0f, 1f)] public float grassFarShade = 0.25f;

    [Header("Shadows")]
    [Tooltip("Screen resolution the voxel sun shadow rays are traced at. Half traces a quarter of the rays; the result is upsampled along depth (and texel edges with Pixelated Shadows).")]
    public ShadowResolution shadowResolution = ShadowResolution.Half;
    [Tooltip("Snap sun shadows to the block texture's texels, so shadow edges are pixelated like the textures (follows Small Scale).")]
    public bool pixelatedShadows = true;
    [Tooltip("Length of the per-pixel shadow rays voxels cast on each other (world units).")]
    [Min(0f)] public float sunShadowDistance = 256f;
    [Tooltip("Surfaces closer to the camera than this trace full resolution shadow rays; farther ones use Far Shadow Detail.")]
    [Min(0f)] public float shadowDetailDistance = 96f;
    [Tooltip("Shadow ray resolution for surfaces beyond Shadow Detail Distance: the half or quarter resolution voxel data (fewer, bigger steps; small shadows within a coarse voxel of the surface disappear) or only 8x8x8 bricks.")]
    public FarShadowDetail farShadowDetail = FarShadowDetail.Half;
    [Tooltip("Surfaces farther from the camera than this get no voxel shadows.")]
    [Min(0f)] public float shadowMaxDistance = 300f;
    [Tooltip("Occluders closer than this (in blocks) along a shadow ray are ignored. Removes the striping that each 1-block step of a slope casts on the next one when the sun is low. 0 = exact voxel shadows.")]
    [Range(0f, 8f)] public float shadowIgnoreDistance = 0f;

    [Header("Water / Translucent")]
    public Color surfaceTint = new(0.55f, 0.8f, 1f, 1f);
    [Range(0f, 1f)] public float surfaceOpacity = 0.25f;
    public Color deepColor = new(0.02f, 0.18f, 0.28f, 1f);
    [Tooltip("Light absorbed per metre travelled through water.")]
    [Range(0f, 2f)] public float absorption = 0.18f;
    [Range(0f, 4f)] public float specular = 1.5f;

    [Header("GPU Memory (applied at start)")]
    [Tooltip("Chunk slots allocated on the GPU at start (grows automatically).")]
    [Min(64)] public int initialChunkCapacity = 2048;
    [Tooltip("8x8x8 voxel pages allocated on the GPU at start, 512 bytes each (grows automatically).")]
    [Min(1024)] public int initialPageCapacity = 65536;
    [Tooltip("Chunk uploads to the GPU per frame (scaled down by the adaptive tuner under load).")]
    [Min(1)] public int uploadsPerFrame = 8;

    static class Ids
    {
        public static readonly int Smoothness = Shader.PropertyToID("_VoxelSmoothness");
        public static readonly int TexVariation = Shader.PropertyToID("_VoxelTexVariation");
        public static readonly int TranslucentTint = Shader.PropertyToID("_VoxelTranslucentTint");
        public static readonly int DeepColor = Shader.PropertyToID("_VoxelDeepColor");
        public static readonly int SurfaceOpacity = Shader.PropertyToID("_VoxelSurfaceOpacity");
        public static readonly int Absorption = Shader.PropertyToID("_VoxelAbsorption");
        public static readonly int Specular = Shader.PropertyToID("_VoxelSpecular");
        public static readonly int SunShadowDistance = Shader.PropertyToID("_VoxelSunShadowDistance");
        public static readonly int ShadowDetailDistance = Shader.PropertyToID("_VoxelShadowDetailDistance");
        public static readonly int FarShadowDetail = Shader.PropertyToID("_VoxelFarShadowDetail");
        public static readonly int ShadowMaxDistance = Shader.PropertyToID("_VoxelShadowMaxDistance");
        public static readonly int ShadowIgnoreDistance = Shader.PropertyToID("_VoxelShadowIgnoreDistance");
        public static readonly int Relief = Shader.PropertyToID("_VoxelRelief");
        public static readonly int ReliefShadows = Shader.PropertyToID("_VoxelReliefShadows");
        public static readonly int ReliefLeaf = Shader.PropertyToID("_VoxelReliefLeaf");
        public static readonly int Grass = Shader.PropertyToID("_VoxelGrass");
        public static readonly int GrassShape = Shader.PropertyToID("_VoxelGrassShape");
        public static readonly int GrassShadows = Shader.PropertyToID("_VoxelGrassShadows");
        public static readonly int GrassPatchHeight = Shader.PropertyToID("_VoxelGrassPatchHeight");
        public static readonly int GrassFar = Shader.PropertyToID("_VoxelGrassFar");
        public static readonly int GrassBlades = Shader.PropertyToID("_VoxelGrassBlades");
        public static readonly int GrassTexels = Shader.PropertyToID("_VoxelGrassTexels");
        public static readonly int TexelCount = Shader.PropertyToID("_VoxelTexelCount");
        public static readonly int SolidLeavesFromLevel = Shader.PropertyToID("_VoxelSolidLeavesFromLevel");
        public static readonly int AmbientOcclusion = Shader.PropertyToID("_VoxelAmbientOcclusion");
        public static readonly int ShadowPixelated = Shader.PropertyToID("_VoxelShadowPixelated");
    }

    public Material OpaqueMaterialOrDefault => opaqueMaterial != null ? opaqueMaterial : Resources.Load<Material>("VoxelRaymarchOpaque");
    public Material TranslucentMaterialOrDefault => translucentMaterial != null ? translucentMaterial : Resources.Load<Material>("VoxelRaymarchTranslucent");

    /// <summary>The settings in the scene, or a default one created at runtime.</summary>
    public static VoxelRenderSettings FindOrCreate()
    {
        var existing = FindFirstObjectByType<VoxelRenderSettings>();
        if (existing != null)
            return existing;

        Debug.LogWarning("[VoxelRenderSettings] None in the scene - using defaults. Add one with GameObject > ProGenWorld > Voxel Render Settings to edit them.");
        return new GameObject("Voxel Render Settings").AddComponent<VoxelRenderSettings>();
    }

    void OnEnable() => Apply();
    void OnDisable()
    {
        RestorePipeline();
        grassBlades?.Release();
        grassBlades = null;
    }
    void OnValidate() => Apply();

    void Update()
    {
        // Cheap; keeps globals correct after domain reloads and when values are animated.
        Apply();
    }

    public void Apply()
    {
        Shader.SetGlobalFloat(Ids.Smoothness, smoothness);
        Shader.SetGlobalVector(Ids.TexVariation, new Vector4(smallScale, largeScale, largeBlend, (float)blendMode));
        Shader.SetGlobalColor(Ids.TranslucentTint, surfaceTint);
        Shader.SetGlobalColor(Ids.DeepColor, deepColor);
        Shader.SetGlobalFloat(Ids.SurfaceOpacity, surfaceOpacity);
        Shader.SetGlobalFloat(Ids.Absorption, absorption);
        Shader.SetGlobalFloat(Ids.Specular, specular);
        Shader.SetGlobalFloat(Ids.SunShadowDistance, sunShadowDistance);
        Shader.SetGlobalFloat(Ids.ShadowDetailDistance, shadowDetailDistance);
        Shader.SetGlobalFloat(Ids.FarShadowDetail, (float)farShadowDetail);
        Shader.SetGlobalFloat(Ids.ShadowMaxDistance, shadowMaxDistance);
        Shader.SetGlobalFloat(Ids.ShadowIgnoreDistance, shadowIgnoreDistance);
        Shader.SetGlobalVector(Ids.Relief, new Vector4(relief ? reliefDepth : 0f, reliefDistance, reliefFade, reliefOcclusion));
        Shader.SetGlobalFloat(Ids.ReliefShadows, reliefShadows ? 1f : 0f);
        Shader.SetGlobalFloat(Ids.ReliefLeaf, relief ? leafThickness : 0f);
        Shader.SetGlobalVector(Ids.Grass, new Vector4(grass ? grassHeight : 0f, grassBladesPerBlock, grassDistance, grassFade));
        Shader.SetGlobalVector(Ids.GrassShape, new Vector4(grassCoverage, grassWidth, grassLean, grassRootShade));
        Shader.SetGlobalFloat(Ids.GrassShadows, grassShadows ? 1f : 0f);
        Shader.SetGlobalFloat(Ids.GrassPatchHeight, grassPatchHeight);
        Shader.SetGlobalFloat(Ids.GrassFar, grassFarShade);
        ApplyGrassBlades();
        Shader.SetGlobalFloat(Ids.SolidLeavesFromLevel, solidLeavesFromLod);
        Shader.SetGlobalFloat(Ids.AmbientOcclusion, ambientOcclusion);
        Shader.SetGlobalFloat(Ids.ShadowPixelated, pixelatedShadows ? 1f : 0f);
        ApplyAntiAliasing();
    }

    GraphicsBuffer grassBlades;
    int bakedTexels, bakedWidth, bakedLean;

    // Blade layouts are baked for the blade grid (block texture texels per block, one voxel =
    // one block) and the width / lean settings; re-baked only when one of them changes.
    void ApplyGrassBlades()
    {
        float texelCount = Shader.GetGlobalFloat(Ids.TexelCount);   // set by VoxelWorldRenderer
        if (texelCount <= 0f)
            texelCount = 16f;
        int texels = Mathf.Clamp(Mathf.RoundToInt(texelCount / Mathf.Max(smallScale, 0.01f)), 2, VoxelGrassBlades.MaxTexels);

        if (grassBlades != null && !grassBlades.IsValid())
            grassBlades = null;
        if (grassBlades == null || texels != bakedTexels || grassWidth != bakedWidth || grassLean != bakedLean)
        {
            grassBlades ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, VoxelGrassBlades.Length, 16);
            grassBlades.SetData(VoxelGrassBlades.Bake(texels, grassWidth, grassLean));
            bakedTexels = texels;
            bakedWidth = grassWidth;
            bakedLean = grassLean;
        }
        Shader.SetGlobalBuffer(Ids.GrassBlades, grassBlades);
        Shader.SetGlobalFloat(Ids.GrassTexels, texels);
    }

    // Only while playing, so edit mode never writes into the scene's camera.
    void ApplyAntiAliasing()
    {
        if (!Application.isPlaying || antiAliasing == AntiAliasing.Camera)
            return;

        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null || !cam.TryGetComponent(out UniversalAdditionalCameraData data))
            return;

        ApplyRenderScale();

        var mode = antiAliasing switch
        {
            AntiAliasing.Fxaa => AntialiasingMode.FastApproximateAntialiasing,
            AntiAliasing.Smaa => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
            AntiAliasing.Temporal => AntialiasingMode.TemporalAntiAliasing,
            _ => AntialiasingMode.None,
        };
        if (data.antialiasing != mode)
        {
            data.antialiasing = mode;
            data.antialiasingQuality = AntialiasingQuality.High;
        }

        // URP silently skips TAA on MSAA targets and on cameras without post processing.
        if (mode == AntialiasingMode.TemporalAntiAliasing)
        {
            cam.allowMSAA = false;
            data.renderPostProcessing = true;
        }
    }

    UniversalRenderPipelineAsset pipeline;
    float originalScale;
    UpscalingFilterSelection originalFilter;

    // URP forces TAA whenever the asset asks for STP upscaling, so the upscaler has to follow the
    // anti-aliasing choice. The asset's own values come back when play mode ends.
    void ApplyRenderScale()
    {
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp == null)
            return;
        if (pipeline != urp)
        {
            RestorePipeline();
            pipeline = urp;
            originalScale = urp.renderScale;
            originalFilter = urp.upscalingFilter;
        }

        var filter = antiAliasing == AntiAliasing.Temporal ? UpscalingFilterSelection.STP : UpscalingFilterSelection.Linear;
        if (urp.upscalingFilter != filter) urp.upscalingFilter = filter;
        if (!Mathf.Approximately(urp.renderScale, renderScale)) urp.renderScale = renderScale;
    }

    void RestorePipeline()
    {
        if (pipeline == null)
            return;
        pipeline.renderScale = originalScale;
        pipeline.upscalingFilter = originalFilter;
        pipeline = null;
    }
}
