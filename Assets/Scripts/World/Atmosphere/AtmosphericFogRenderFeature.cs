using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class AtmosphericFogRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public Material fogMaterial = null;
    }

    public Settings settings = new Settings();
    private AtmosphericFogRenderPass fogPass;

    public override void Create()
    {
        fogPass = new AtmosphericFogRenderPass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.fogMaterial == null)
        {
            Debug.LogWarning("Atmospheric Fog Material is not assigned!");
            return;
        }

        renderer.EnqueuePass(fogPass);
    }

    protected override void Dispose(bool disposing)
    {
        fogPass?.Dispose();
    }

    class AtmosphericFogRenderPass : ScriptableRenderPass
    {
        private Settings settings;
        private Material fogMaterial;
        private static AtmosphericFogController cachedController;

        // Shader property IDs
        private static readonly int FogStartDistanceID = Shader.PropertyToID("_FogStartDistance");
        private static readonly int FogEndDistanceID = Shader.PropertyToID("_FogEndDistance");
        private static readonly int MaxFogDensityID = Shader.PropertyToID("_MaxFogDensity");
        private static readonly int FogBaseHeightID = Shader.PropertyToID("_FogBaseHeight");
        private static readonly int FogFalloffID = Shader.PropertyToID("_FogFalloff");
        private static readonly int HeightFogDensityID = Shader.PropertyToID("_HeightFogDensity");
        private static readonly int RayleighIntensityID = Shader.PropertyToID("_RayleighIntensity");
        private static readonly int ScatteringCoeffID = Shader.PropertyToID("_ScatteringCoeff");
        private static readonly int FogColorID = Shader.PropertyToID("_FogColor");
        private static readonly int SkyColorID = Shader.PropertyToID("_SkyColor");
        private static readonly int SunColorID = Shader.PropertyToID("_SunColor");
        private static readonly int SunDirectionID = Shader.PropertyToID("_SunDirection");
        private static readonly int HorizonHeightID = Shader.PropertyToID("_HorizonHeight");

        private class PassData
        {
            internal Material fogMaterial;
        }

        public AtmosphericFogRenderPass(Settings settings)
        {
            this.settings = settings;
            this.renderPassEvent = settings.renderPassEvent;
            this.fogMaterial = settings.fogMaterial;

            ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            if (fogMaterial == null)
                return;

            // Find fog controller globally (not on camera)
            if (cachedController == null)
            {
                cachedController = Object.FindFirstObjectByType<AtmosphericFogController>();
            }

            if (cachedController == null || !cachedController.enabled)
                return;

            var source = resourceData.activeColorTexture;
            if (!source.IsValid())
                return;

            var descriptor = cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;
            descriptor.msaaSamples = 1;

            TextureHandle tempColor = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph,
                descriptor,
                "_TempColorTexture",
                false
            );

            // Copy source to temp first (preserve original)
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Copy to Temp", out var passData))
            {
                builder.UseTexture(source);
                builder.SetRenderAttachment(tempColor, 0);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, source, new Vector4(1, 1, 0, 0), 0, false);
                });
            }

            // Apply fog from temp to source
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Apply Fog", out var passData))
            {
                passData.fogMaterial = fogMaterial;

                builder.UseTexture(tempColor);
                builder.SetRenderAttachment(source, 0);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    UpdateShaderProperties(data.fogMaterial, cachedController);
                    Blitter.BlitTexture(context.cmd, tempColor, new Vector4(1, 1, 0, 0), data.fogMaterial, 0);
                });
            }
        }

        private void UpdateShaderProperties(Material material, AtmosphericFogController controller)
        {
            material.SetFloat(FogStartDistanceID, controller.fogStartDistance);
            material.SetFloat(FogEndDistanceID, controller.fogEndDistance);
            material.SetFloat(MaxFogDensityID, controller.maxFogDensity);
            material.SetFloat(FogBaseHeightID, controller.fogBaseHeight);
            material.SetFloat(FogFalloffID, controller.fogFalloff);
            material.SetFloat(HeightFogDensityID, controller.enableHeightFog ? controller.heightFogDensity : 0f);
            material.SetFloat(RayleighIntensityID, controller.enableRayleighScattering ? controller.rayleighIntensity : 0f);
            material.SetVector(ScatteringCoeffID, controller.scatteringCoefficients * 0.0001f);
            material.SetColor(FogColorID, controller.fogColor);
            material.SetColor(SkyColorID, controller.skyColor);
            material.SetFloat(HorizonHeightID, controller.horizonHeight);

            if (controller.sunLight != null)
            {
                if (controller.useSunLightColor)
                {
                    material.SetColor(SunColorID, controller.sunLight.color * controller.sunLight.intensity);
                }
                else
                {
                    material.SetColor(SunColorID, controller.sunColor);
                }
                material.SetVector(SunDirectionID, -controller.sunLight.transform.forward);
            }
            else
            {
                material.SetColor(SunColorID, controller.sunColor);
                material.SetVector(SunDirectionID, Vector3.down);
            }
        }

        public void Dispose()
        {
        }
    }
}