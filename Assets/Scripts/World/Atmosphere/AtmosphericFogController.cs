using UnityEngine;

[ExecuteInEditMode]
public class AtmosphericFogController : MonoBehaviour
{
    [Header("Fog Distance Settings")]
    [Tooltip("Distance where fog starts")]
    public float fogStartDistance = 50f;
    [Tooltip("Distance where fog reaches maximum")]
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

    [Header("Sun Settings")]
    [Tooltip("Main directional light (sun)")]
    public Light sunLight;
    [Tooltip("Use sun light color automatically")]
    public bool useSunLightColor = true;

    private FogPresetManager presetManager;

    void OnEnable()
    {
        // Try to find preset manager
        presetManager = GetComponent<FogPresetManager>();

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