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
    [Tooltip("Rendering switches to 1/8 resolution voxels (8 blocks wide) beyond this many chunks. Rendering only: these chunks are still Far. 0 = never.")]
    [Min(0)] public int lowestDetailRange = 24;
    [Tooltip("Upload only the detail levels a chunk's LOD draws: Mid chunks skip full resolution, Far chunks also skip half resolution. Saves most of the GPU voxel memory; a chunk is re-uploaded when its LOD changes.")]
    public bool trimGpuDetail = true;

    [Header("Horizon (beyond Lowest Detail Range)")]
    [Tooltip("Chunks first generated beyond Lowest Detail Range + Upgrade Margin become Horizon chunks: terrain noise at the lowest detail level only (1/8 resolution for 64 block chunks), no decorations, no block data kept. They are regenerated in full when they come within range. Loaded chunks are never downgraded.")]
    public bool lowResHorizon = true;
    [Tooltip("Chunks inside Lowest Detail Range + this margin are regenerated in full, so the full data is ready before rendering wants more than the lowest detail level.")]
    [Min(0)] public int horizonUpgradeMargin = 2;

    [Header("Far Horizon (beyond the view distance)")]
    [Tooltip("Draw a coarse, blocky copy of the terrain surface beyond the loaded chunks, in rings of growing voxel size. No caves, overhangs or decorations.")]
    public bool farHorizon = true;
    [Tooltip("Rings. Each ring's voxels are twice as wide as the ring inside it and it reaches about twice as far.")]
    [Range(1, 4)] public int horizonRings = 3;
    [Tooltip("Blocks per voxel in the first ring.")]
    [Min(1)] public int horizonVoxelSize = 16;
    [Tooltip("Tiles (64 voxels each) every ring reaches on each side of the player, at least.")]
    [Range(1, 6)] public int horizonRingRadius = 2;
    [Tooltip("World heights searched for the horizon's terrain surface. Terrain outside this range is clipped.")]
    public float horizonMinHeight = -256f;
    public float horizonMaxHeight = 768f;
    [Tooltip("End the fog at the far horizon instead of at the edge of the loaded chunks. Off: the horizon sits in full fog.")]
    public bool fogToHorizon = true;

    [Header("Block Data (CPU memory)")]
    [Tooltip("How Mid chunks keep their full resolution blocks. Near chunks always keep them as plain arrays (physics reads them every frame). Compressed data is exact: it is expanded whenever something needs it (writes, repacks, physics).")]
    public BlockStorage midBlockStorage = BlockStorage.Compressed;
    [Tooltip("How Far chunks keep their full resolution blocks.")]
    public BlockStorage farBlockStorage = BlockStorage.Compressed;
    [Tooltip("Seconds a chunk's blocks must go unused before they are compressed. Avoids compressing chunks that neighbours are still decorating into.")]
    [Min(0f)] public float compressAfterSeconds = 5f;
    [Tooltip("Main thread milliseconds per frame spent compressing block arrays.")]
    [Min(0.05f)] public float compressBudgetMs = 0.5f;

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

    /// <summary>Chunks further than this (chunks) are generated as Horizon chunks; 0 = off.</summary>
    public float HorizonDistance => lowResHorizon && lowestDetailRange > 0 ? lowestDetailRange + horizonUpgradeMargin : 0f;

    /// <summary>How a chunk at this LOD keeps its full resolution blocks.</summary>
    public BlockStorage StorageFor(LODLevel lod) => lod switch
    {
        LODLevel.Mid => midBlockStorage,
        LODLevel.Far => farBlockStorage,
        _ => BlockStorage.Full
    };

    /// <summary>Finest GPU detail level uploaded for a chunk at this LOD (0 = everything).</summary>
    public int MinGpuLevel(LODLevel lod)
    {
        if (!trimGpuDetail)
            return 0;
        return lod == LODLevel.Horizon ? 3 : lod == LODLevel.Far ? 2 : (lod == LODLevel.Mid ? 1 : 0);
    }

    // Chunk distances are measured to chunk centres, so the first missing terrain can be as close
    // as half a chunk inside the view distance.
    public float LoadedEdgeDistance => Mathf.Max(0f, viewDistance - 0.5f) * ChunkSize;

    /// <summary>World size of a far horizon tile in a ring (64 voxels of the ring's size).</summary>
    public float HorizonTileSize(int ring) => VoxelGpuFormat.Resolution(ChunkSize) * (float)(horizonVoxelSize << ring);

    /// <summary>Distance the far horizon reaches at least (0 when off).</summary>
    public float HorizonEdgeDistance => farHorizon ? horizonRingRadius * HorizonTileSize(Mathf.Clamp(horizonRings, 1, 4) - 1) : 0f;

    public float FogEndDistance => Mathf.Max(1f, (fogToHorizon && farHorizon ? Mathf.Max(LoadedEdgeDistance, HorizonEdgeDistance) : LoadedEdgeDistance) - fogEndInset);

    public float FogStartDistance => Mathf.Clamp(fogStartDistance, 0f, FogEndDistance - 1f);

    public float FarClipDistance => Mathf.Max((UnloadDistance + 1f) * ChunkSize,
        farHorizon ? (HorizonEdgeDistance + HorizonTileSize(Mathf.Clamp(horizonRings, 1, 4) - 1)) * 1.42f : 0f) + farClipMargin;

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
        if (lowestDetailRange > 0 && lowestDetailRange < midRange)
            lowestDetailRange = midRange;
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
