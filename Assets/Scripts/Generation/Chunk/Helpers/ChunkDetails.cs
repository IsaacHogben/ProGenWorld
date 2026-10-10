using System;

/// <summary>
/// Detail level of a chunk, chosen by ChunkManager from the distance to the player
/// (nearRange / midRange). Other systems use it to decide how much work a chunk gets
/// (e.g. ECS decorations only spawn on Near chunks). Rendering picks its own GPU detail level
/// per draw cell from the same distances, so a LOD change never re-uploads a chunk.
/// </summary>
public enum LODLevel : byte
{
    Near = 0,
    Mid = 1,
    Far = 2
}

public struct LODSettings
{
    public int voxelScale;      // blocks per voxel of the GPU detail level used at this LOD (1, 2, 4)
    public int sampleRes;       // step size of noise sample - influences size of array

    [NonSerialized] public int densityCount; // Computed at runtime based on chunkSize/stride
}
