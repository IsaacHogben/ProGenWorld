using UnityEngine;

[ExecuteInEditMode]
public class AtmosphericFogController : MonoBehaviour
{
    [Header("Fog Distance Settings")]
    [Tooltip("View distance / LOD settings the fog distances follow while their Link Fog is on. Found automatically when empty.")]
    public ViewDistanceSettings viewDistanceSettings;
    [Tooltip("Distance where fog starts (unused while linked to View Distance Settings)")]
    public float fogStartDistance = 50f;
    [Tooltip("Distance where fog reaches maximum (unused while linked to View Distance Settings)")]
    public float fogEndDistance = 500f;
    [Tooltip("Maximum fog density at far distance")]
    [Range(0f, 1f)]
    public float maxFogDensity = 1f;

    [Header("Height Fog Settings")]
    [Tooltip("Enable height-based fog")]
    public bool enableHeightFog = true;
    [Tooltip("Height where fog is densest")]
    public float fogBaseHeight = 0f;
    [Tooltip("How quickly fog fades with height")]
    public float fogFalloff = 0.05f;
    [Tooltip("Height fog density multiplier")]
    [Range(0f, 2f)]
    public float heightFogDensity = 1f;

    [Header("Rayleigh Scattering")]
    [Tooltip("Enable Rayleigh atmospheric scattering")]
    public bool enableRayleighScattering = true;
    [Tooltip("Intensity of Rayleigh scattering")]
    [Range(0f, 5f)]
    public float rayleighIntensity = 1f;
    [Tooltip("Scattering coefficients for RGB")]
    public Vector3 scatteringCoefficients = new Vector3(5.8f, 13.5f, 33.1f);

    [Header("Fog Colors")]
    [Tooltip("Primary fog color (used for distance fog)")]
    public Color fogColor = new Color(0.5f, 0.6f, 0.7f, 1f);
    [Tooltip("Horizon/sky color for Rayleigh effect")]
    public Color skyColor = new Color(0.4f, 0.6f, 0.9f, 1f);
    [Tooltip("Sun/light direction color")]
    public Color sunColor = new Color(1f, 0.9f, 0.7f, 1f);

    [Header("Sky/Fog Transition")]
    [Tooltip("World-space Y height where sky transitions to fog")]
    public float horizonHeight = 0f;

    [Header("Clouds")]
    [Tooltip("Pixel art clouds on the sky: chunky texels fixed to the sky, in four flat tones.")]
    public bool enableClouds = true;
    [Tooltip("Size of one cloud texel (degrees of sky). About 0.25 is 4 to 5 screen pixels at 1080p.")]
    [Range(0.05f, 1.5f)] public float cloudPixelSize = 0.25f;
    [Tooltip("How many cloud cells fit around the horizon (more = smaller, more numerous clouds).")]
    [Range(2, 40)] public int cloudCount = 10;
    [Tooltip("Share of the horizon with cumulus on it.")]
    [Range(0f, 1f)] public float cloudCoverage = 0.55f;
    [Tooltip("Height of the tallest cumulus towers (degrees above their base).")]
    [Range(1f, 60f)] public float cloudTowerHeight = 28f;
    [Tooltip("Size of the round puffs the cumulus are built from (degrees of sky). Keep them well above the cloud pixel size so they stay round.")]
    [Range(1f, 20f)] public float cloudPuffSize = 7f;
    [Tooltip("Blend from lumpy noise puffs (0) to round, sphere shaded puffs (1).")]
    [Range(0f, 1f)] public float cloudRoundness = 0.35f;
    [Tooltip("Size of the lumps (the noise puffs) on the clouds (degrees of sky), whatever the cloud count.")]
    [Range(2f, 30f)] public float cloudLumpSize = 8f;
    [Tooltip("Elevation of the cumulus bases (degrees; slightly below the horizon hides the gap behind distant hills).")]
    [Range(-10f, 30f)] public float cloudBaseElevation = -1f;
    [Tooltip("Wind drift of the clouds around the sky (degrees per second).")]
    [Range(-2f, 2f)] public float cloudDrift = 0.08f;
    [Tooltip("Share of thin, high streak clouds.")]
    [Range(0f, 1f)] public float streakCoverage = 0.55f;
    [Tooltip("Elevation of the streak layer (degrees).")]
    [Range(0f, 80f)] public float streakElevation = 32f;
    [Tooltip("Thickness of the streak layer (degrees).")]
    [Range(1f, 40f)] public float streakThickness = 10f;
    [Tooltip("How much clouds low on the horizon fade into the haze.")]
    [Range(0f, 1f)] public float cloudHorizonHaze = 0.5f;
    [Tooltip("Clouds below this elevation (degrees) fade into the haze.")]
    [Range(0.1f, 40f)] public float cloudHazeHeight = 8f;
    [Tooltip("Ragged pixel edges: how far (in texels) the cloud outline is dithered.")]
    [Range(0f, 3f)] public float cloudEdgeDither = 1f;
    [Tooltip("Cloud tone: sunlit tops.")]
    public Color cloudLit = new Color(1f, 1f, 1f, 1f);
    [Tooltip("Cloud tone: light.")]
    public Color cloudLight = new Color(0.86f, 0.92f, 1f, 1f);
    [Tooltip("Cloud tone: mid.")]
    public Color cloudMid = new Color(0.68f, 0.8f, 0.96f, 1f);
    [Tooltip("Cloud tone: shadowed undersides.")]
    public Color cloudShadow = new Color(0.5f, 0.64f, 0.88f, 1f);

    [Header("Sun Settings")]
    [Tooltip("Main directional light (sun)")]
    public Light sunLight;
    [Tooltip("Use sun light color automatically")]
    public bool useSunLightColor = true;

    private FogPresetManager presetManager;

    bool LinkedToViewDistance => viewDistanceSettings != null && viewDistanceSettings.linkFog;

    /// <summary>Fog start the shader uses: from View Distance Settings when linked, otherwise fogStartDistance.</summary>
    public float EffectiveFogStartDistance => LinkedToViewDistance ? viewDistanceSettings.FogStartDistance : fogStartDistance;

    /// <summary>Fog end the shader uses: from View Distance Settings when linked, otherwise fogEndDistance.</summary>
    public float EffectiveFogEndDistance => LinkedToViewDistance ? viewDistanceSettings.FogEndDistance : fogEndDistance;

    void OnEnable()
    {
        // Try to find preset manager
        presetManager = GetComponent<FogPresetManager>();

        if (viewDistanceSettings == null)
            viewDistanceSettings = GetComponent<ViewDistanceSettings>();
        if (viewDistanceSettings == null)
            viewDistanceSettings = FindFirstObjectByType<ViewDistanceSettings>();

        // Auto-find sun if not set
        if (sunLight == null)
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    sunLight = light;
                    break;
                }
            }
        }
    }

    // Time of day is now managed by TimeOfDayManager
    // Fog controller just displays based on current settings

    // Public methods for external control (kept for compatibility)
    public void SetFogColor(Color color)
    {
        fogColor = color;
    }

    public void SetSkyColor(Color color)
    {
        skyColor = color;
    }

    public void SetSunColor(Color color)
    {
        sunColor = color;
        useSunLightColor = false;
    }
}