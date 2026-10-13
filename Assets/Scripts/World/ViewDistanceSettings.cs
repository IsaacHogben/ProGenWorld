using UnityEngine;

/// <summary>
/// View distance, LOD ranges and the fog / camera values derived from them. Put one on its own
/// GameObject (GameObject > ProGenWorld > View Distance Settings). ChunkManager reads the chunk
/// ranges from here, and AtmosphericFogController takes its fog distances from here while
/// Link Fog is on: the start is set here, the end follows the view distance so fog always fully
/// covers the edge of the loaded terrain.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ViewDistanceSettings : MonoBehaviour
{
    [Header("View Distance (chunks)")]
    [Tooltip("Chunks are loaded out to this distance from the player (measured to chunk centres).")]
    [Min(1)] public int viewDistance = 20;
    [Tooltip("Extra chunks past the view distance before loaded chunks are unloaded.")]
    [Min(0)] public int unloadPastViewDistance = 1;

    [Header("LOD (chunks)")]
    [Tooltip("Chunks closer than this are Near: full detail, ECS decorations.")]
    [Min(0)] public int nearRange = 7;
    [Tooltip("Chunks closer than this are Mid; anything further is Far. Rendering switches to 1/2 and 1/4 resolution voxels at the same distances.")]
    [Min(0)] public int midRange = 14;

    [Header("Fog Link")]
    [Tooltip("Use the fog start below and end the fog at the view distance, instead of the fog controller / preset distances.")]
    public bool linkFog = true;
    [Tooltip("World units from the camera where fog begins. Kept below the linked fog end.")]
    [Min(0f)] public float fogStartDistance = 448f;
    [Tooltip("World units before the nearest unloaded terrain where fog reaches full density.")]
    [Min(0f)] public float fogEndInset = 16f;

    [Header("Camera")]
    [Tooltip("Set the main camera's far clip plane from the view distance while playing.")]
    public bool driveCameraFarClip = true;
    [Tooltip("World units added past the furthest loaded chunk.")]
    [Min(0f)] public float farClipMargin = 64f;
    [Tooltip("Camera to drive. Empty = Camera.main.")]
    public Camera targetCamera;

    [Tooltip("Source of the chunk size. Found automatically when empty.")]
    [SerializeField] ChunkManager chunkManager;

    public int ChunkSize
    {
        get
        {
            if (chunkManager == null)
                chunkManager = FindFirstObjectByType<ChunkManager>();
            return chunkManager != null ? chunkManager.chunkSize : 32;
        }
    }

    public int UnloadDistance => viewDistance + unloadPastViewDistance;

    // Chunk distances are measured to chunk centres, so the first missing terrain can be as close
    // as half a chunk inside the view distance.
    public float LoadedEdgeDistance => Mathf.Max(0f, viewDistance - 0.5f) * ChunkSize;

    public float FogEndDistance => Mathf.Max(1f, LoadedEdgeDistance - fogEndInset);

    public float FogStartDistance => Mathf.Clamp(fogStartDistance, 0f, FogEndDistance - 1f);

    public float FarClipDistance => (UnloadDistance + 1f) * ChunkSize + farClipMargin;

    /// <summary>The settings in the scene, or a default one created at runtime.</summary>
    public static ViewDistanceSettings FindOrCreate()
    {
        var existing = FindFirstObjectByType<ViewDistanceSettings>();
        if (existing != null)
            return existing;

        Debug.LogWarning("[ViewDistanceSettings] None in the scene - using defaults. Add one with GameObject > ProGenWorld > View Distance Settings to edit them.");
        return new GameObject("View Distance Settings").AddComponent<ViewDistanceSettings>();
    }

    void OnValidate()
    {
        if (midRange < nearRange)
            midRange = nearRange;
    }

    void LateUpdate()
    {
        if (!driveCameraFarClip || !Application.isPlaying)
            return;

        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam != null)
            cam.farClipPlane = FarClipDistance;
    }
}
