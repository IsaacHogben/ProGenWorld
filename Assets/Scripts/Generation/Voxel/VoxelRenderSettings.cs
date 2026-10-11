using UnityEngine;

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

    [Header("Shadows")]
    [Tooltip("Length of the per-pixel shadow rays voxels cast on each other (world units).")]
    [Min(0f)] public float sunShadowDistance = 256f;
    [Tooltip("Surfaces closer to the camera than this trace full resolution shadow rays; farther ones only test 8x8x8 bricks.")]
    [Min(0f)] public float shadowDetailDistance = 96f;
    [Tooltip("Surfaces farther from the camera than this get no voxel shadows.")]
    [Min(0f)] public float shadowMaxDistance = 512f;
    [Tooltip("Occluders closer than this (in blocks) along a shadow ray are ignored. Removes the striping that each 1-block step of a slope casts on the next one when the sun is low. 0 = exact voxel shadows.")]
    [Range(0f, 8f)] public float shadowIgnoreDistance = 0f;
    [Tooltip("Render voxels near the camera into the URP shadow map. Normally off: the terrain shadows itself with block data rays, and meshes using the 'ProGenWorld/Lit (Voxel Shadows)' shader receive terrain shadows the same way. Only needed for materials that can't use that shader.")]
    public bool castIntoShadowMap = false;
    [Tooltip("Distance for the option above. Raymarching shadow maps is expensive - keep this small.")]
    [Min(0f)] public float shadowMapDistance = 32f;
    [Tooltip("Minimum distance (blocks) voxel casters are pushed away from the light in the URP shadow map. Removes shadow acne (diagonal lines on the ground). The push is never less than Shadow Ignore Distance. Larger values make terrain shadows on the player start a little further from the caster.")]
    [Range(0.5f, 4f)] public float shadowMapBias = 1f;

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
        public static readonly int ShadowMaxDistance = Shader.PropertyToID("_VoxelShadowMaxDistance");
        public static readonly int ShadowIgnoreDistance = Shader.PropertyToID("_VoxelShadowIgnoreDistance");
        public static readonly int ShadowMapBias = Shader.PropertyToID("_VoxelShadowMapBias");
        public static readonly int Relief = Shader.PropertyToID("_VoxelRelief");
        public static readonly int ReliefShadows = Shader.PropertyToID("_VoxelReliefShadows");
        public static readonly int ReliefLeaf = Shader.PropertyToID("_VoxelReliefLeaf");
        public static readonly int SolidLeavesFromLevel = Shader.PropertyToID("_VoxelSolidLeavesFromLevel");
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
        Shader.SetGlobalFloat(Ids.ShadowMaxDistance, shadowMaxDistance);
        Shader.SetGlobalFloat(Ids.ShadowIgnoreDistance, shadowIgnoreDistance);
        Shader.SetGlobalFloat(Ids.ShadowMapBias, shadowMapBias);
        Shader.SetGlobalVector(Ids.Relief, new Vector4(relief ? reliefDepth : 0f, reliefDistance, reliefFade, reliefOcclusion));
        Shader.SetGlobalFloat(Ids.ReliefShadows, reliefShadows ? 1f : 0f);
        Shader.SetGlobalFloat(Ids.ReliefLeaf, relief ? leafThickness : 0f);
        Shader.SetGlobalFloat(Ids.SolidLeavesFromLevel, solidLeavesFromLod);
    }
}
