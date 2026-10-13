using System;

/// <summary>
/// Detail level of a chunk, chosen by ChunkManager from the distance to the player
/// (nearRange / midRange / lowestDetailRange). Other systems use it to decide how much work a
/// chunk gets (e.g. ECS decorations only spawn on Near chunks). Rendering picks its own GPU
/// detail level per draw cell from the same distances.
/// Near, Mid and Far chunks have full resolution blocks and decorations. Horizon chunks are
/// generated at the lowest GPU detail level only (1/8 resolution for 64 block chunks), without
/// decorations or block data, and are regenerated in full when they come closer.
/// </summary>
public enum LODLevel : byte
{
    Near = 0,
    Mid = 1,
    Far = 2,
    Horizon = 3
}

public struct LODSettings
{
    public int voxelScale;      // blocks per voxel of the GPU detail level used at this LOD (1, 2, 4, 8)
    public int sampleRes;       // step size of noise sample - influences size of array

    [NonSerialized] public int densityCount; // Computed at runtime based on chunkSize/stride
}
