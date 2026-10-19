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
    public enum WaterReflections { Off = 0, Sky = 1, ScreenSpace = 2 }

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

    [Header("Lighting")]
    [Tooltip("Sunlight on the terrain in this many flat bands instead of a smooth falloff: every lit face snaps up to the next band, shadow stays shadow (the textures are kept). Painterly / anime look. 0 = smooth.")]
    [Range(0, 6)] public int terrainLightBands = 3;

    [Header("Leaves")]
    [Tooltip("Leaves as flat colour pixel art: solid faces in the leaf's average colour, lit in a few flat bands by how much sun reaches them (their direction and the voxel shadow), plus the sky light. Leaf planes (Leaf Cross Size) are lit as if facing away from the canopy.")]
    public bool leafFlatTones = true;
    [Tooltip("Number of sunlight bands on flat colour leaves (shadow, ..., full sun).")]
    [Range(2, 4)] public int leafToneBands = 3;
    [Tooltip("Share of texels one band brighter, as a hint of leaf detail on flat colour leaves.")]
    [Range(0f, 0.2f)] public float leafToneSparkle = 0.05f;
    [Tooltip("Light wrap on flat colour leaves: leaves facing partly away from the sun still count as lit, so a low sun still gives sunlit canopy tops. 0 = plain facing ratio.")]
    [Range(0f, 1f)] public float leafLightWrap = 0.5f;
    [Tooltip("Sunlight the darkest (shadowed) band of flat colour leaves keeps, as a share of the full sun, so shadowed leaves are not near black.")]
    [Range(0f, 0.6f)] public float leafShadowLift = 0.2f;
    [Tooltip("Saturation of the flat leaf colour (1 = the leaf texture's hue as it is, lower = greyer, which also lets the warm sun tint show).")]
    [Range(0f, 2f)] public float leafSaturation = 0.75f;
    [Tooltip("Brightness (linear luminance) of the flat leaf colour. The colour keeps the leaf texture's hue but not its brightness, which averages out very dark.")]
    [Range(0.02f, 0.6f)] public float leafBrightness = 0.2f;
    [Tooltip("Colour tint of sunlit flat colour leaves (multiplies the leaf colour): warm yellow-green tops, as in painted canopies.")]
    [ColorUsage(false, true)] public Color leafSunTint = new Color(1.25f, 1.2f, 0.55f, 1f);
    [Tooltip("Colour tint of shadowed flat colour leaves and the dark fill (multiplies the leaf colour): cool teal undersides.")]
    [ColorUsage(false, true)] public Color leafShadowTint = new Color(0.6f, 0.9f, 1.05f, 1f);
    [Tooltip("Leafy texture on flat colour leaves: clumps of this many block texture texels drawn a band lighter or darker, so canopies (the distant cube leaves especially) are not flat colour.")]
    [Range(1f, 8f)] public float leafClumpTexels = 3f;
    [Tooltip("Share of leaf clumps a band lighter.")]
    [Range(0f, 0.5f)] public float leafClumpLight = 0.22f;
    [Tooltip("Share of leaf clumps a band darker.")]
    [Range(0f, 0.5f)] public float leafClumpDark = 0.25f;
    [Tooltip("Leaf clumps on the near leaf planes too (they already have see-through edges); off: distant cube leaves only.")]
    public bool leafClumpsOnPlanes = false;
    [Tooltip("How dark the filled texels of leaf planes are (the see-through texels past the see-through distance or depth): 0 = the plane's own tone, so far canopies have no borders; 1 = the dark fill, which outlines every leaf plane (a cel shaded look).")]
    [Range(0f, 1f)] public float leafFillDarkness = 0f;
    [Tooltip("Brightness of the dark fill that replaces the see-through texels of leaf planes past the see-through distance (share of the shadowed tone).")]
    [Range(0f, 1f)] public float leafFillBrightness = 0.5f;
    [Tooltip("Leaf blocks drawn as three crossed planes through their centre instead of a cube, this many blocks across, so they reach past the block into the air around it. Solid in the middle, see-through towards the ragged pixel art edges. Level 0 only (further out: cubes). 0 = cubes.")]
    [Range(0f, 1.9f)] public float leafCrossSize = 1.5f;
    [Tooltip("Leaf planes are see-through within this camera distance (world units). Further out their see-through texels are filled with a dark leaf colour, so rays stop there (cheaper), up to the solid leaves distance where leaves become cubes.")]
    [Min(0f)] public float leafSeeThroughDistance = 48f;
    [Tooltip("Camera rays see through the leaf planes of this many leaf blocks; deeper in the canopy the see-through texels are filled with the dark leaf colour, so rays stop instead of marching through whole trees (much cheaper up close; the canopy edge stays see-through).")]
    [Range(1, 6)] public int leafSeeThroughDepth = 2;
    [Tooltip("Where the see-through edge of a leaf plane starts, as a share of its radius: solid inside, more and more see-through texels towards the outline.")]
    [Range(0f, 1f)] public float leafEdgeStart = 0.55f;
    [Tooltip("Share of see-through texels in the solid middle of a leaf plane.")]
    [Range(0f, 0.5f)] public float leafInnerHoles = 0.04f;
    [Tooltip("The planes of a leaf block shadow each other where they reach past the block. Off: a leaf plane is only shadowed by other leaves and terrain, so the flat tone bands follow the canopy shape.")]
    public bool leafSelfShadow = false;

    /// <summary>The flat leaf look BlockSurface.AutoLeafColours reproduces in a leaf palette.</summary>
    public BlockSurface.LeafLook LeafLook => new BlockSurface.LeafLook
    {
        brightness = leafBrightness,
        saturation = leafSaturation,
        sunTint = leafSunTint,
        shadowTint = leafShadowTint,
    };

    [Header("Level Of Detail")]
    [Tooltip("Leaves and other alpha masked blocks render solid from this detail level on (1 = half resolution, 2 = quarter resolution). Rays stop at the first leaf instead of marching through the canopy; far away the mask holes are smaller than a pixel anyway. 0 = never.")]
    [Range(0, 2)] public int solidLeavesFromLod = 1;
    [Tooltip("Camera rays also treat leaves as solid beyond this distance (world units), at any detail level: the holes are about pixel sized there, and marching through canopies is expensive. 0 = only from the LOD above.")]
    [Min(0f)] public float solidLeavesDistance = 64f;

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

    [Header("Pixel Art Distance")]
    [Tooltip("Voxels render in bands by distance, each band as square pixels of its own size with one ray per pixel (far fewer rays) and its own anti-aliasing. Voxels nearer than the first band, and all objects, stay at full resolution. Play mode only. The main camera's TAA / STP still run over the result and soften it: Anti Aliasing None or FXAA and Render Scale 1 keep the bands as set.")]
    public bool pixelArtDistance = true;
    [Tooltip("Pixel art bands (up to 4): each from its distance on until the next band's, with its own pixel size and anti-aliasing. A band at distance 0 pixelates the voxels right around the player (objects' shadows on those voxels are lost).")]
    public VoxelFarLayer.BandSettings[] pixelBands =
    {
        new() { distance = 48f, pixelSize = 2, antiAliasing = VoxelFarLayer.AntiAliasing.None, filter = VoxelFarLayer.Filter.Sharp, snap = true },
        new() { distance = 112f, pixelSize = 4, antiAliasing = VoxelFarLayer.AntiAliasing.None, filter = VoxelFarLayer.Filter.Sharp, snap = true },
    };

    [Header("Wind (grass and leaves)")]
    [Tooltip("How hard the wind moves grass and leaves at a wind of length 1. The wind itself is Wind Direction under Water: it blows along that vector and its length scales this. 0 = still.")]
    [Range(0f, 1f)] public float windStrength = 0.6f;
    [Tooltip("Size of the gusts drifting across the world (blocks).")]
    [Min(0.5f)] public float gustSize = 12f;
    [Tooltip("How fast gusts drift downwind (blocks per second).")]
    [Min(0f)] public float gustSpeed = 4f;
    [Tooltip("Wind animation updates per second: low values move in visible pixel steps. 0 = smooth.")]
    [Range(0f, 30f)] public float windStepRate = 8f;
    [Tooltip("Most a grass blade tip bends in full wind (texels).")]
    [Range(0f, 4f)] public float grassSway = 2f;
    [Tooltip("Most leaf clusters shift in full wind (alpha mask texels).")]
    [Range(0f, 3f)] public float leafFlutter = 1f;
    [Tooltip("Leaves closer to the camera than this (world units) flutter. Farther, the motion is below a pixel and only costs time (every leaf a ray or shadow ray passes tests it).")]
    [Min(0f)] public float leafFlutterDistance = 48f;

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
    [Tooltip("Shadow rays count as blocked once they reach this many leaf blocks in a row of a canopy, instead of testing every leaf's alpha: a canopy a few blocks deep lets almost no light through anyway. Lower = cheaper, darker canopies. 0 = test every leaf.")]
    [Range(0, 8)] public int shadowLeafDepth = 3;

    [Header("Water / Translucent")]
    [Tooltip("Multiplies the water block's painted texture (Block Database), which colours the thin surface layer.")]
    public Color surfaceTint = Color.white;
    [Range(0f, 1f)] public float surfaceOpacity = 0.25f;
    public Color deepColor = new(0.02f, 0.18f, 0.28f, 1f);
    [Tooltip("Light absorbed per metre travelled through water, seen from above the surface.")]
    [Range(0f, 2f)] public float absorption = 0.18f;
    [Tooltip("Absorption used while the camera is inside water: lower than Absorption so you can see around underwater.")]
    [Range(0f, 2f)] public float underwaterAbsorption = 0.06f;
    [Range(0f, 4f)] public float specular = 1.5f;
    [Tooltip("Random brightness and opacity on the water surface: half per texel, on the same pixel grid as the terrain textures (follows Small Scale), and half per block. 0 = plain colour.")]
    [Range(0f, 1f)] public float pixelNoise = 0.6f;
    [Tooltip("Tilt of the animated ripples on horizontal water faces at a wind of length 1, one facet per texture texel. Bends the reflections and the sun highlight. 0 = flat mirror.")]
    [Range(0f, 1f)] public float waveStrength = 0.25f;
    [Tooltip("Shore foam: water surface texels where the water below is shallower than this (blocks) turn to foam, with a dithered pixel edge. 0 = off.")]
    [Range(0f, 2f)] public float foamWidth = 0.6f;
    public Color foamColor = new(0.85f, 0.95f, 0.95f, 1f);
    [Tooltip("Speed of the ripples at a wind of length 1.")]
    [Range(0f, 4f)] public float waveSpeed = 1f;
    [Tooltip("Wind over the water in world XZ (x = east, y = north). Ripples travel along it; its length scales their height and speed (0 = calm, flat water). Meant to be set by a weather system.")]
    public Vector2 windDirection = new(1f, 0.5f);

    [Header("Water Reflections")]
    [Tooltip("Off: no reflections. Sky: reflect the sky colours only (nearly free). Screen Space: also reflect terrain that is on screen, marched against the depth buffer, falling back to the sky (needs Opaque + Depth Texture on the URP asset).")]
    public WaterReflections reflections = WaterReflections.ScreenSpace;
    [Tooltip("Scales the Fresnel reflection: faint looking straight down, mirror-like at grazing angles.")]
    [Range(0f, 1f)] public float reflectionStrength = 1f;
    [Tooltip("Farthest reflected object (world units) for screen space reflections.")]
    [Min(1f)] public float reflectionDistance = 80f;
    [Tooltip("Depth buffer samples per screen space reflection ray. Steps grow with distance; fewer is cheaper but misses thin objects.")]
    [Range(4, 48)] public int reflectionSteps = 16;
    [Tooltip("Sky colours reflected where screen space reflections find nothing. Taken from the Atmospheric Fog Controller when there is one in the scene.")]
    public Color skyZenith = new(0.4f, 0.6f, 0.9f, 1f);
    public Color skyHorizon = new(0.5f, 0.6f, 0.7f, 1f);

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
        public static readonly int WaterSurface = Shader.PropertyToID("_VoxelWaterSurface");
        public static readonly int WaterWind = Shader.PropertyToID("_VoxelWaterWind");
        public static readonly int WaterFoam = Shader.PropertyToID("_VoxelWaterFoam");
        public static readonly int WaterReflection = Shader.PropertyToID("_VoxelWaterReflection");
        public static readonly int SkyZenith = Shader.PropertyToID("_VoxelSkyZenith");
        public static readonly int SkyHorizon = Shader.PropertyToID("_VoxelSkyHorizon");
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
        public static readonly int Wind = Shader.PropertyToID("_VoxelWind");
        public static readonly int WindAnim = Shader.PropertyToID("_VoxelWindAnim");
        public static readonly int LeafFlutterDistance = Shader.PropertyToID("_VoxelLeafFlutterDistance");
        public static readonly int GrassBlades = Shader.PropertyToID("_VoxelGrassBlades");
        public static readonly int GrassTexels = Shader.PropertyToID("_VoxelGrassTexels");
        public static readonly int TexelCount = Shader.PropertyToID("_VoxelTexelCount");
        public static readonly int SolidLeavesFromLevel = Shader.PropertyToID("_VoxelSolidLeavesFromLevel");
        public static readonly int SolidLeavesDistance = Shader.PropertyToID("_VoxelSolidLeavesDistance");
        public static readonly int TerrainBands = Shader.PropertyToID("_VoxelTerrainBands");
        public static readonly int LeafTones = Shader.PropertyToID("_VoxelLeafTones");
        public static readonly int LeafCross = Shader.PropertyToID("_VoxelLeafCross");
        public static readonly int LeafLight = Shader.PropertyToID("_VoxelLeafLight");
        public static readonly int LeafLight2 = Shader.PropertyToID("_VoxelLeafLight2");
        public static readonly int LeafSunTint = Shader.PropertyToID("_VoxelLeafSunTint");
        public static readonly int LeafShadowTint = Shader.PropertyToID("_VoxelLeafShadowTint");
        public static readonly int LeafSeeThroughDepth = Shader.PropertyToID("_VoxelLeafSeeThroughDepth");
        public static readonly int LeafClumps = Shader.PropertyToID("_VoxelLeafClumps");
        public static readonly int LeafSelfShadow = Shader.PropertyToID("_VoxelLeafSelfShadow");
        public static readonly int ShadowLeafDepth = Shader.PropertyToID("_VoxelShadowLeafDepth");
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
        farLayer?.Dispose();
        farLayer = null;
        RestorePipeline();
        grassBlades?.Release();
        grassBlades = null;
    }
    void OnValidate() => Apply();

    void Update()
    {
        // Cheap; keeps globals correct after domain reloads and when values are animated.
        Apply();
        ApplyFarLayer();
    }

    VoxelFarLayer farLayer;

    // Not from OnValidate: it creates a camera.
    void ApplyFarLayer()
    {
        var main = Application.isPlaying && pixelArtDistance ? (targetCamera != null ? targetCamera : Camera.main) : null;
        if (farLayer != null && farLayer.Main != main)
        {
            farLayer.Dispose();
            farLayer = null;
        }
        if (main == null)
            return;
        farLayer ??= new VoxelFarLayer(main);
        farLayer.Configure(pixelBands);
    }

    public void Apply()
    {
        Shader.SetGlobalFloat(Ids.Smoothness, smoothness);
        Shader.SetGlobalVector(Ids.TexVariation, new Vector4(smallScale, largeScale, largeBlend, (float)blendMode));
        Shader.SetGlobalColor(Ids.TranslucentTint, surfaceTint);
        Shader.SetGlobalColor(Ids.DeepColor, deepColor);
        Shader.SetGlobalFloat(Ids.SurfaceOpacity, surfaceOpacity);
        Shader.SetGlobalFloat(Ids.Absorption, CameraUnderwater() ? underwaterAbsorption : absorption);
        Shader.SetGlobalFloat(Ids.Specular, specular);
        ApplyWind();
        Shader.SetGlobalVector(Ids.WaterFoam, new Vector4(foamColor.linear.r, foamColor.linear.g, foamColor.linear.b, foamWidth));
        Shader.SetGlobalVector(Ids.WaterReflection, new Vector4((float)reflections, reflectionStrength, reflectionDistance, reflectionSteps));
        ApplySkyColors();
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
        // Same wind as the water (ApplyWind): direction of windDirection, strength scaled by its length.
        float windLength = windDirection.magnitude;
        Vector2 windDir = windLength > 1e-4f ? windDirection / windLength : Vector2.right;
        Shader.SetGlobalVector(Ids.Wind, new Vector4(windDir.x, windDir.y, gustSize, gustSpeed));
        Shader.SetGlobalVector(Ids.WindAnim, new Vector4(windStrength * Mathf.Min(windLength, 2f), windStepRate, grassSway, leafFlutter));
        Shader.SetGlobalFloat(Ids.LeafFlutterDistance, leafFlutterDistance);
        ApplyGrassBlades();
        Shader.SetGlobalFloat(Ids.SolidLeavesFromLevel, solidLeavesFromLod);
        Shader.SetGlobalFloat(Ids.SolidLeavesDistance, solidLeavesDistance);
        Shader.SetGlobalFloat(Ids.TerrainBands, terrainLightBands);
        Shader.SetGlobalVector(Ids.LeafTones, new Vector4(leafFlatTones ? 1f : 0f, leafToneBands, leafToneSparkle, leafFillBrightness));
        Shader.SetGlobalVector(Ids.LeafCross, new Vector4(leafCrossSize, leafSeeThroughDistance, leafEdgeStart, leafInnerHoles));
        Shader.SetGlobalVector(Ids.LeafLight, new Vector4(leafLightWrap, leafShadowLift, leafSaturation, leafBrightness));
        Shader.SetGlobalVector(Ids.LeafLight2, new Vector4(leafFillDarkness, 0f, 0f, 0f));
        Shader.SetGlobalVector(Ids.LeafSunTint, (Vector4)leafSunTint);
        Shader.SetGlobalVector(Ids.LeafShadowTint, (Vector4)leafShadowTint);
        Shader.SetGlobalFloat(Ids.LeafSeeThroughDepth, leafSeeThroughDepth);
        Shader.SetGlobalVector(Ids.LeafClumps, new Vector4(leafClumpTexels, leafClumpLight, leafClumpDark, leafClumpsOnPlanes ? 1f : 0f));
        Shader.SetGlobalFloat(Ids.LeafSelfShadow, leafSelfShadow ? 1f : 0f);
        Shader.SetGlobalFloat(Ids.ShadowLeafDepth, shadowLeafDepth);
        Shader.SetGlobalFloat(Ids.AmbientOcclusion, ambientOcclusion);
        Shader.SetGlobalFloat(Ids.ShadowPixelated, pixelatedShadows ? 1f : 0f);
        ApplyAntiAliasing();
    }

    // Whether the rendering camera is inside a water block (Underwater Absorption applies).
    bool CameraUnderwater()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null || !VoxelPhysics.IsReady)
            return false;
        Vector3 p = cam.transform.position;
        return VoxelPhysics.IsWater(new Unity.Mathematics.int3(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));
    }

    float wavePhase;
    float lastWaveTime = -1f;

    // The ripple phase is advanced here rather than from time in the shader, so changing the wind
    // speeds the waves up or down without making them jump.
    void ApplyWind()
    {
        float wind = windDirection.magnitude;
        float now = Time.time;
        if (lastWaveTime >= 0f && now > lastWaveTime)
            wavePhase = (wavePhase + (now - lastWaveTime) * waveSpeed * wind) % 10000f;
        lastWaveTime = now;

        Vector2 dir = wind > 1e-4f ? windDirection / wind : Vector2.right;
        Shader.SetGlobalVector(Ids.WaterSurface, new Vector4(pixelNoise, wavePhase, waveStrength * wind, 0f));
        Shader.SetGlobalVector(Ids.WaterWind, new Vector4(dir.x, dir.y, 0f, 0f));
    }

    AtmosphericFogController fog;
    int fogSearchFrame = -1000;

    // Reflections fall back to the sky the fog pass paints, when there is one.
    void ApplySkyColors()
    {
        if (fog == null && Time.frameCount - fogSearchFrame > 120)
        {
            fogSearchFrame = Time.frameCount;
            fog = FindFirstObjectByType<AtmosphericFogController>();
        }
        bool useFog = fog != null && fog.isActiveAndEnabled;
        Shader.SetGlobalColor(Ids.SkyZenith, useFog ? fog.skyColor : skyZenith);
        Shader.SetGlobalColor(Ids.SkyHorizon, useFog ? fog.fogColor : skyHorizon);
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
