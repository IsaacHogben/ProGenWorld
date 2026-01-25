using UnityEngine;

public class TimeOfDayManager : MonoBehaviour
{
    [Header("Time Settings")]
    [Tooltip("Current time of day (0-24 hours)")]
    [Range(0f, 24f)]
    public float timeOfDay = 12f;

    [Tooltip("Enable automatic time progression")]
    public bool autoProgress = false;

    [Tooltip("Speed of time cycle (hours per second)")]
    public float timeSpeed = 0.1f;

    [Header("Sun Settings")]
    [Tooltip("Main directional light (sun)")]
    public Light sunLight;

    [Tooltip("Sun rotation axis offset")]
    public float sunAxisRotation = 170f;

    [Tooltip("Fade sun intensity when below horizon")]
    public bool fadeSunIntensity = true;

    [Tooltip("Maximum sun intensity during day")]
    public float maxSunIntensity = 1.0f;

    [Tooltip("Minimum sun intensity at night (usually 0)")]
    public float minSunIntensity = 0.0f;

    [Header("Ambient Light Settings")]
    [Tooltip("Control ambient light intensity based on time")]
    public bool controlAmbientLight = true;

    [Tooltip("Ambient intensity during day")]
    public float dayAmbientIntensity = 1.0f;

    [Tooltip("Ambient intensity at night")]
    public float nightAmbientIntensity = 0.3f;

    [Tooltip("Day ambient color")]
    public Color dayAmbientColor = new Color(0.5f, 0.6f, 0.7f);

    [Tooltip("Night ambient color")]
    public Color nightAmbientColor = new Color(0.2f, 0.2f, 0.3f);
    [Header("Fog Preset Integration")]
    [Tooltip("Automatically change fog presets based on time")]
    public bool controlFogPresets = true;

    [Tooltip("Fog preset manager to control")]
    public FogPresetManager fogPresetManager;

    [Header("Time Period Definitions")]
    [Tooltip("When dawn starts (hours)")]
    public float dawnStart = 5f;
    [Tooltip("When day starts (hours)")]
    public float dayStart = 7f;
    [Tooltip("When dusk starts (hours)")]
    public float duskStart = 17f;
    [Tooltip("When night starts (hours)")]
    public float nightStart = 19f;

    // Public properties for other scripts to read
    public enum TimePeriod { Night, Dawn, Day, Dusk }
    public TimePeriod CurrentPeriod { get; private set; }
    public float NormalizedTime => timeOfDay / 24f;

    private TimePeriod lastPeriod = TimePeriod.Night;

    void Start()
    {
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

        // Auto-find fog preset manager if not set
        if (fogPresetManager == null)
        {
            fogPresetManager = FindFirstObjectByType<FogPresetManager>();
        }

        // Initialize
        UpdateSunPosition();
        UpdateTimePeriod();
        UpdateAmbientLight();
    }

    void Update()
    {
        if (autoProgress)
        {
            // Progress time
            timeOfDay += timeSpeed * Time.deltaTime;
            if (timeOfDay >= 24f)
            {
                timeOfDay -= 24f;
                OnDayComplete();
            }

            UpdateSunPosition();
            UpdateTimePeriod();
            UpdateAmbientLight();
        }
    }

    void UpdateSunPosition()
    {
        if (sunLight == null) return;

        // Calculate sun angle based on time (0-360 degrees over 24 hours)
        float sunAngle = (timeOfDay / 24f) * 360f - 90f;
        sunLight.transform.rotation = Quaternion.Euler(sunAngle, sunAxisRotation, 0f);

        // Fade sun intensity based on angle (below horizon = no light)
        if (fadeSunIntensity)
        {
            // Get the sun's up direction (Y component of its forward vector)
            float sunHeight = -sunLight.transform.forward.y;

            // Fade intensity based on height above/below horizon
            // sunHeight > 0 = above horizon, < 0 = below horizon
            float intensity;
            if (sunHeight > 0)
            {
                // Above horizon - fade in from horizon (0) to fully up (1)
                intensity = Mathf.Clamp01(sunHeight * 2f); // Multiply by 2 for faster fade
                intensity = Mathf.Lerp(minSunIntensity, maxSunIntensity, intensity);
            }
            else
            {
                // Below horizon - no light
                intensity = minSunIntensity;
            }

            sunLight.intensity = intensity;
        }
    }

    void UpdateAmbientLight()
    {
        if (!controlAmbientLight) return;

        // Calculate ambient light based on sun height
        float sunHeight = 0f;
        if (sunLight != null)
        {
            sunHeight = -sunLight.transform.forward.y;
        }

        // Interpolate ambient intensity and color based on sun height
        float t = Mathf.Clamp01(sunHeight * 2f + 0.5f); // -0.5 to 0.5 maps to 0 to 1

        float ambientIntensity = Mathf.Lerp(nightAmbientIntensity, dayAmbientIntensity, t);
        Color ambientColor = Color.Lerp(nightAmbientColor, dayAmbientColor, t);

        // Apply to render settings
        RenderSettings.ambientIntensity = ambientIntensity;
        RenderSettings.ambientLight = ambientColor;
    }

    void UpdateTimePeriod()
    {
        // Determine current time period
        TimePeriod newPeriod;

        if (timeOfDay >= dawnStart && timeOfDay < dayStart)
        {
            newPeriod = TimePeriod.Dawn;
        }
        else if (timeOfDay >= dayStart && timeOfDay < duskStart)
        {
            newPeriod = TimePeriod.Day;
        }
        else if (timeOfDay >= duskStart && timeOfDay < nightStart)
        {
            newPeriod = TimePeriod.Dusk;
        }
        else
        {
            newPeriod = TimePeriod.Night;
        }

        // Check if period changed
        if (newPeriod != lastPeriod)
        {
            CurrentPeriod = newPeriod;
            OnTimePeriodChanged(newPeriod);
            lastPeriod = newPeriod;
        }
    }

    void OnTimePeriodChanged(TimePeriod newPeriod)
    {
        // Apply fog presets based on time period
        if (controlFogPresets && fogPresetManager != null)
        {
            switch (newPeriod)
            {
                case TimePeriod.Dawn:
                    // Transition to day preset during dawn
                    if (fogPresetManager.presets.Length > 0)
                        fogPresetManager.ApplyPreset(0, true); // Clear Day
                    break;

                case TimePeriod.Day:
                    if (fogPresetManager.presets.Length > 0)
                        fogPresetManager.ApplyPreset(0, true); // Clear Day
                    break;

                case TimePeriod.Dusk:
                    if (fogPresetManager.presets.Length > 2)
                        fogPresetManager.ApplyPreset(2, true); // Sunset
                    break;

                case TimePeriod.Night:
                    if (fogPresetManager.presets.Length > 3)
                        fogPresetManager.ApplyPreset(3, true); // Night
                    break;
            }
        }

        // Broadcast event for other systems
        Debug.Log($"Time period changed to: {newPeriod} at {timeOfDay:F1} hours");
    }

    void OnDayComplete()
    {
        Debug.Log("Day cycle complete!");
    }

    // Public methods for external control
    public void SetTime(float hours)
    {
        timeOfDay = Mathf.Clamp(hours, 0f, 24f);
        UpdateSunPosition();
        UpdateTimePeriod();
        UpdateAmbientLight();
    }

    public void SetTimePeriod(TimePeriod period)
    {
        switch (period)
        {
            case TimePeriod.Dawn:
                SetTime((dawnStart + dayStart) / 2f);
                break;
            case TimePeriod.Day:
                SetTime((dayStart + duskStart) / 2f);
                break;
            case TimePeriod.Dusk:
                SetTime((duskStart + nightStart) / 2f);
                break;
            case TimePeriod.Night:
                SetTime(0f);
                break;
        }
    }

    public float GetTimeInPeriod()
    {
        // Returns 0-1 representing progress through current period
        switch (CurrentPeriod)
        {
            case TimePeriod.Dawn:
                return Mathf.InverseLerp(dawnStart, dayStart, timeOfDay);
            case TimePeriod.Day:
                return Mathf.InverseLerp(dayStart, duskStart, timeOfDay);
            case TimePeriod.Dusk:
                return Mathf.InverseLerp(duskStart, nightStart, timeOfDay);
            case TimePeriod.Night:
                if (timeOfDay >= nightStart)
                    return Mathf.InverseLerp(nightStart, 24f, timeOfDay);
                else
                    return Mathf.InverseLerp(0f, dawnStart, timeOfDay) * 0.5f + 0.5f;
            default:
                return 0f;
        }
    }
}