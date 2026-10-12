using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Sun shadows for the whole screen, right after URP's depth + normals prepass (which already
/// marched every voxel pixel):
///  1. traces a voxel shadow ray per pixel of a full / half / quarter resolution texture, for
///     terrain and objects alike (VoxelScreenShadows.shader pass 0),
///  2. upsamples that and multiplies in URP's main light shadow map, which only holds objects
///     (voxels never render into it), into URP's _ScreenSpaceShadowmapTexture (pass 1),
///  3. switches the opaques to URP's screen space shadow mode, so URP Lit objects receive both.
/// The voxel terrain reads the same texture. So voxels shadow themselves and objects only
/// through the rays, and objects shadow everything through the shadow map: nothing is shadowed
/// twice. VoxelScreenShadowsRestorePass switches transparents back to the regular shadow map.
///
/// Also tells the voxel forward pass it may rebuild its hits from the prepass instead of marching.
/// Requests the normals texture, so URP always runs the DepthNormals prepass for this camera.
/// Enqueued per camera by VoxelWorldRenderer; while it does not run (no prepass), the voxel forward
/// pass marches and traces its shadows itself.
/// </summary>
public sealed class VoxelScreenPass : ScriptableRenderPass
{
    static readonly int VoxelShadowTextureId = Shader.PropertyToID("_VoxelScreenShadowTexture");
    static readonly int ResolvedTextureId = Shader.PropertyToID("_ScreenSpaceShadowmapTexture");
    static readonly int ScreenShadowsId = Shader.PropertyToID("_VoxelScreenShadows");
    static readonly int PrepassReuseId = Shader.PropertyToID("_VoxelPrepassReuse");
    static readonly int ObjectShadowsId = Shader.PropertyToID("_VoxelObjectShadows");

    internal static readonly GlobalKeyword MainLightShadows = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
    internal static readonly GlobalKeyword MainLightShadowCascades = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
    internal static readonly GlobalKeyword MainLightShadowScreen = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_SCREEN");

    /// <summary>Voxel shadow texture size divisor: 1 = full resolution, 2 = half, 4 = quarter.</summary>
    public int downsample = 2;

    /// <summary>Let the voxel forward pass rebuild its hits from the prepass.</summary>
    public bool reusePrepass = true;

    readonly Material material;
    static readonly ProfilingSampler RaysSampler = new("Voxel Shadow Rays");
    static readonly ProfilingSampler ResolveSampler = new("Voxel Shadow Resolve");

    class PassData
    {
        public Material material;
        public float reuse;
        public float objectShadows;
        public bool cascades;
    }

    public VoxelScreenPass(Material material)
    {
        this.material = material;
        // After URP's Screen Space Shadows feature (AfterRenderingPrePasses + 1; kept on the renderer
        // so builds keep the _MAIN_LIGHT_SHADOWS_SCREEN shader variants): this texture replaces its.
        renderPassEvent = RenderPassEvent.AfterRenderingPrePasses + 2;
        profilingSampler = new ProfilingSampler("Voxel Screen Shadows");
        ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
    }

    /// <summary>Call before each camera renders: nothing is reusable until this pass has run.</summary>
    public static void ResetGlobals()
    {
        Shader.SetGlobalFloat(ScreenShadowsId, 0f);
        Shader.SetGlobalFloat(PrepassReuseId, 0f);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resources = frameData.Get<UniversalResourceData>();
        var cameraData = frameData.Get<UniversalCameraData>();
        var shadowData = frameData.Get<UniversalShadowData>();
        if (material == null || !resources.cameraDepthTexture.IsValid() || !resources.cameraNormalsTexture.IsValid())
            return;

        var target = cameraData.cameraTargetDescriptor;
        int k = Mathf.Clamp(downsample, 1, 4);
        TextureHandle voxelShadows = renderGraph.CreateTexture(new TextureDesc((target.width + k - 1) / k, (target.height + k - 1) / k)
        {
            colorFormat = GraphicsFormat.R32G32B32A32_SFloat,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            clearBuffer = false,
            name = "_VoxelScreenShadowTexture"
        });
        TextureHandle resolved = renderGraph.CreateTexture(new TextureDesc(target.width, target.height)
        {
            colorFormat = GraphicsFormat.R8_UNorm,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            clearBuffer = false,
            name = "_ScreenSpaceShadowmapTexture"
        });

        // 1. Voxel shadow rays.
        using (var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Shadow Rays", out var data, RaysSampler))
        {
            data.material = material;
            builder.UseAllGlobalTextures(true);
            builder.UseTexture(resources.cameraDepthTexture);
            builder.UseTexture(resources.cameraNormalsTexture);
            builder.SetRenderAttachment(voxelShadows, 0, AccessFlags.WriteAll);
            builder.SetGlobalTextureAfterPass(voxelShadows, VoxelShadowTextureId);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.material, 0));
        }

        // 2. Upsample + object shadow map, 3. screen space shadow mode for the opaques.
        using (var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Shadow Resolve", out var data, ResolveSampler))
        {
            data.material = material;
            data.reuse = reusePrepass ? 1f : 0f;
            data.objectShadows = shadowData.supportsMainLightShadows ? 1f : 0f;
            data.cascades = shadowData.mainLightShadowCascadesCount > 1;
            builder.UseAllGlobalTextures(true);
            builder.UseTexture(voxelShadows);
            builder.UseTexture(resources.cameraDepthTexture);
            builder.UseTexture(resources.cameraNormalsTexture);
            if (resources.mainShadowsTexture.IsValid())
                builder.UseTexture(resources.mainShadowsTexture);
            builder.SetRenderAttachment(resolved, 0, AccessFlags.WriteAll);
            builder.SetGlobalTextureAfterPass(resolved, ResolvedTextureId);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                // The resolve samples the shadow map itself: regular (cascade) keywords for it,
                // whatever URP's Screen Space Shadows pass switched them to.
                ctx.cmd.SetKeyword(MainLightShadowScreen, false);
                ctx.cmd.SetKeyword(MainLightShadows, !d.cascades);
                ctx.cmd.SetKeyword(MainLightShadowCascades, d.cascades);
                ctx.cmd.SetGlobalFloat(ObjectShadowsId, d.objectShadows);
                Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.material, 1);

                ctx.cmd.SetKeyword(MainLightShadows, false);
                ctx.cmd.SetKeyword(MainLightShadowCascades, false);
                ctx.cmd.SetKeyword(MainLightShadowScreen, true);
                ctx.cmd.SetGlobalFloat(ScreenShadowsId, 1f);
                ctx.cmd.SetGlobalFloat(PrepassReuseId, d.reuse);
            });
        }
    }
}

/// <summary>
/// Before the transparents: back from screen space shadows to URP's regular main light shadow
/// keywords (transparent surfaces are not in the depth prepass). Their shadow map holds objects
/// only, so transparents get object shadows but no terrain shadows from it.
/// </summary>
public sealed class VoxelScreenShadowsRestorePass : ScriptableRenderPass
{
    class PassData
    {
        public bool shadows, cascades;
    }

    public VoxelScreenShadowsRestorePass()
    {
        renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        profilingSampler = new ProfilingSampler("Voxel Screen Shadows Restore");
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resources = frameData.Get<UniversalResourceData>();
        var shadowData = frameData.Get<UniversalShadowData>();
        if (!resources.cameraDepthTexture.IsValid() || !resources.cameraNormalsTexture.IsValid())
            return;   // the screen pass did not run either

        using var builder = renderGraph.AddRasterRenderPass<PassData>("Voxel Screen Shadows Restore", out var data, profilingSampler);
        data.shadows = shadowData.supportsMainLightShadows && shadowData.mainLightShadowCascadesCount == 1;
        data.cascades = shadowData.supportsMainLightShadows && shadowData.mainLightShadowCascadesCount > 1;
        builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
        builder.AllowPassCulling(false);
        builder.AllowGlobalStateModification(true);
        builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
        {
            ctx.cmd.SetKeyword(VoxelScreenPass.MainLightShadowScreen, false);
            ctx.cmd.SetKeyword(VoxelScreenPass.MainLightShadows, d.shadows);
            ctx.cmd.SetKeyword(VoxelScreenPass.MainLightShadowCascades, d.cascades);
        });
    }
}
