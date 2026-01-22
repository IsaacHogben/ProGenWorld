using UnityEngine;

[System.Serializable]
public class FogPreset
{
    public string presetName;

    [Header("Distance Fog")]
    public float fogStartDistance = 50f;
    public float fogEndDistance = 500f;
    public float maxFogDensity = 1f;

    [Header("Height Fog")]
    public bool enableHeightFog = true;
    public float fogBaseHeight = 0f;
    public float fogFalloff = 0.05f;
    public float heightFogDensity = 1f;

    [Header("Rayleigh Scattering")]
    public bool enableRayleighScattering = true;
    public float rayleighIntensity = 1f;
    public Vector3 scatteringCoefficients = new Vector3(5.8f, 13.5f, 33.1f);

    [Header("Colors")]
    public Color fogColor = new Color(0.5f, 0.6f, 0.7f, 1f);
    public Color skyColor = new Color(0.4f, 0.6f, 0.9f, 1f);
    public Color sunColor = new Color(1f, 0.9f, 0.7f, 1f);
}

public class FogPresetManager : MonoBehaviour
{
    [Header("References")]
    public AtmosphericFogController fogController;

    [Header("Presets")]
    public FogPreset[] presets;

    [Header("Current Preset")]
    public int currentPresetIndex = 0;

    [Header("Transition")]
    public bool smoothTransition = true;
    public float transitionDuration = 2f;

    private bool isTransitioning = false;
    private float transitionProgress = 0f;
    private FogPreset fromPreset;
    private FogPreset toPreset;

    void Start()
    {
        if (fogController == null)
        {
            fogController = GetComponent<AtmosphericFogController>();
        }

        // Create default presets if none exist
        if (presets == null || presets.Length == 0)
        {
            CreateDefaultPresets();
        }

        // Apply initial preset
        if (presets.Length > 0)
        {
            ApplyPreset(currentPresetIndex, false);
        }
    }

    void Update()
    {
        if (isTransitioning)
        {
            transitionProgress += Time.deltaTime / transitionDuration;

            if (transitionProgress >= 1f)
            {
                transitionProgress = 1f;
                isTransitioning = false;
            }

            LerpPresets(fromPreset, toPreset, transitionProgress);
        }
    }

    public void ApplyPreset(int index, bool smooth = true)
    {
        if (index < 0 || index >= presets.Length || fogController == null)
            return;

        currentPresetIndex = index;

        if (smooth && smoothTransition)
        {
            StartTransition(presets[index]);
        }
        else
        {
            ApplyPresetImmediate(presets[index]);
        }
    }

    public void ApplyPresetByName(string name, bool smooth = true)
    {
        for (int i = 0; i < presets.Length; i++)
        {
            if (presets[i].presetName == name)
            {
                ApplyPreset(i, smooth);
                return;
            }
        }
        Debug.LogWarning($"Preset '{name}' not found!");
    }

    void StartTransition(FogPreset target)
    {
        fromPreset = CaptureCurrentSettings();
        toPreset = target;
        transitionProgress = 0f;
        isTransitioning = true;
    }

    void ApplyPresetImmediate(FogPreset preset)
    {
        fogController.fogStartDistance = preset.fogStartDistance;
        fogController.fogEndDistance = preset.fogEndDistance;
        fogController.maxFogDensity = preset.maxFogDensity;

        fogController.enableHeightFog = preset.enableHeightFog;
        fogController.fogBaseHeight = preset.fogBaseHeight;
        fogController.fogFalloff = preset.fogFalloff;
        fogController.heightFogDensity = preset.heightFogDensity;

        fogController.enableRayleighScattering = preset.enableRayleighScattering;
        fogController.rayleighIntensity = preset.rayleighIntensity;
        fogController.scatteringCoefficients = preset.scatteringCoefficients;

        fogController.fogColor = preset.fogColor;
        fogController.skyColor = preset.skyColor;
        fogController.sunColor = preset.sunColor;
        fogController.useSunLightColor = false;
    }

    void LerpPresets(FogPreset from, FogPreset to, float t)
    {
        fogController.fogStartDistance = Mathf.Lerp(from.fogStartDistance, to.fogStartDistance, t);
        fogController.fogEndDistance = Mathf.Lerp(from.fogEndDistance, to.fogEndDistance, t);
        fogController.maxFogDensity = Mathf.Lerp(from.maxFogDensity, to.maxFogDensity, t);

        fogController.fogBaseHeight = Mathf.Lerp(from.fogBaseHeight, to.fogBaseHeight, t);
        fogController.fogFalloff = Mathf.Lerp(from.fogFalloff, to.fogFalloff, t);
        fogController.heightFogDensity = Mathf.Lerp(from.heightFogDensity, to.heightFogDensity, t);

        fogController.rayleighIntensity = Mathf.Lerp(from.rayleighIntensity, to.rayleighIntensity, t);
        fogController.scatteringCoefficients = Vector3.Lerp(from.scatteringCoefficients, to.scatteringCoefficients, t);

        fogController.fogColor = Color.Lerp(from.fogColor, to.fogColor, t);
        fogController.skyColor = Color.Lerp(from.skyColor, to.skyColor, t);
        fogController.sunColor = Color.Lerp(from.sunColor, to.sunColor, t);

        // Apply boolean settings from target when transition is >50%
        if (t > 0.5f)
        {
            fogController.enableHeightFog = to.enableHeightFog;
            fogController.enableRayleighScattering = to.enableRayleighScattering;
        }
    }

    FogPreset CaptureCurrentSettings()
    {
        FogPreset preset = new FogPreset();

        preset.fogStartDistance = fogController.fogStartDistance;
        preset.fogEndDistance = fogController.fogEndDistance;
        preset.maxFogDensity = fogController.maxFogDensity;

        preset.enableHeightFog = fogController.enableHeightFog;
        preset.fogBaseHeight = fogController.fogBaseHeight;
        preset.fogFalloff = fogController.fogFalloff;
        preset.heightFogDensity = fogController.heightFogDensity;

        preset.enableRayleighScattering = fogController.enableRayleighScattering;
        preset.rayleighIntensity = fogController.rayleighIntensity;
        preset.scatteringCoefficients = fogController.scatteringCoefficients;

        preset.fogColor = fogController.fogColor;
        preset.skyColor = fogController.skyColor;
        preset.sunColor = fogController.sunColor;

        return preset;
    }

    void CreateDefaultPresets()
    {
        presets = new FogPreset[4];

        // Clear Day
        presets[0] = new FogPreset
        {
            presetName = "Clear Day",
            fogStartDistance = 100f,
            fogEndDistance = 800f,
            maxFogDensity = 0.6f,
            enableHeightFog = false,
            fogFalloff = 0.02f,
            rayleighIntensity = 1.5f,
            fogColor = new Color(0.7f, 0.8f, 0.9f),
            skyColor = new Color(0.4f, 0.7f, 1f),
            sunColor = new Color(1f, 0.95f, 0.85f)
        };

        // Foggy Morning
        presets[1] = new FogPreset
        {
            presetName = "Foggy Morning",
            fogStartDistance = 20f,
            fogEndDistance = 200f,
            maxFogDensity = 0.95f,
            enableHeightFog = true,
            fogBaseHeight = 0f,
            fogFalloff = 0.08f,
            heightFogDensity = 1.5f,
            rayleighIntensity = 0.5f,
            fogColor = new Color(0.85f, 0.88f, 0.9f),
            skyColor = new Color(0.75f, 0.8f, 0.85f),
            sunColor = new Color(1f, 0.9f, 0.7f)
        };

        // Sunset
        presets[2] = new FogPreset
        {
            presetName = "Sunset",
            fogStartDistance = 50f,
            fogEndDistance = 500f,
            maxFogDensity = 0.75f,
            enableHeightFog = true,
            fogBaseHeight = -10f,
            fogFalloff = 0.04f,
            heightFogDensity = 0.8f,
            rayleighIntensity = 2f,
            fogColor = new Color(0.9f, 0.6f, 0.5f),
            skyColor = new Color(1f, 0.5f, 0.4f),
            sunColor = new Color(1f, 0.6f, 0.3f)
        };

        // Night
        presets[3] = new FogPreset
        {
            presetName = "Night",
            fogStartDistance = 30f,
            fogEndDistance = 300f,
            maxFogDensity = 0.85f,
            enableHeightFog = true,
            fogBaseHeight = 0f,
            fogFalloff = 0.05f,
            heightFogDensity = 1f,
            rayleighIntensity = 0.3f,
            fogColor = new Color(0.15f, 0.15f, 0.25f),
            skyColor = new Color(0.05f, 0.05f, 0.15f),
            sunColor = new Color(0.3f, 0.3f, 0.4f)
        };
    }
}