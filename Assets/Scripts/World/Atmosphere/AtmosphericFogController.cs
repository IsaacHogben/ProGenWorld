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

    [Header("Sun Settings")]
    [Tooltip("Main directional light (sun)")]
    public Light sunLight;
    [Tooltip("Use sun light color automatically")]
    public bool useSunLightColor = true;

    [Header("Time of Day")]
    [Tooltip("Current time of day (0-24 hours)")]
    [Range(0f, 24f)]
    public float timeOfDay = 12f;
    [Tooltip("Enable automatic time of day cycling")]
    public bool autoTimeOfDay = false;
    [Tooltip("Speed of time cycle (hours per second)")]
    public float timeSpeed = 0.1f;

    private Camera cam;

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("AtmosphericFogController must be attached to a Camera!");
            enabled = false;
            return;
        }

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

    void Update()
    {
        if (autoTimeOfDay)
        {
            timeOfDay += timeSpeed * Time.deltaTime;
            if (timeOfDay >= 24f) timeOfDay -= 24f;

            UpdateTimeOfDay();
        }
    }

    void UpdateTimeOfDay()
    {
        // Update sun rotation based on time
        if (sunLight != null)
        {
            float sunAngle = (timeOfDay / 24f) * 360f - 90f;
            sunLight.transform.rotation = Quaternion.Euler(sunAngle, 170f, 0f);
        }

        // Update colors based on time of day
        float normalizedTime = timeOfDay / 24f;

        // Dawn: 5-7, Day: 7-17, Dusk: 17-19, Night: 19-5
        if (timeOfDay >= 5f && timeOfDay < 7f)
        {
            // Dawn
            float t = (timeOfDay - 5f) / 2f;
            fogColor = Color.Lerp(new Color(0.3f, 0.3f, 0.4f), new Color(0.6f, 0.7f, 0.8f), t);
            skyColor = Color.Lerp(new Color(0.4f, 0.3f, 0.5f), new Color(0.4f, 0.6f, 0.9f), t);
            sunColor = Color.Lerp(new Color(1f, 0.5f, 0.3f), new Color(1f, 0.95f, 0.8f), t);
        }
        else if (timeOfDay >= 7f && timeOfDay < 17f)
        {
            // Day
            fogColor = new Color(0.6f, 0.7f, 0.8f);
            skyColor = new Color(0.4f, 0.6f, 0.9f);
            sunColor = new Color(1f, 0.95f, 0.8f);
        }
        else if (timeOfDay >= 17f && timeOfDay < 19f)
        {
            // Dusk
            float t = (timeOfDay - 17f) / 2f;
            fogColor = Color.Lerp(new Color(0.6f, 0.7f, 0.8f), new Color(0.3f, 0.3f, 0.4f), t);
            skyColor = Color.Lerp(new Color(0.4f, 0.6f, 0.9f), new Color(0.5f, 0.3f, 0.4f), t);
            sunColor = Color.Lerp(new Color(1f, 0.95f, 0.8f), new Color(1f, 0.4f, 0.2f), t);
        }
        else
        {
            // Night
            fogColor = new Color(0.2f, 0.2f, 0.3f);
            skyColor = new Color(0.1f, 0.1f, 0.2f);
            sunColor = new Color(0.3f, 0.3f, 0.4f);
        }
    }

    // Public methods for external control
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

    public void SetTimeOfDay(float time)
    {
        timeOfDay = Mathf.Clamp(time, 0f, 24f);
        UpdateTimeOfDay();
    }
}