using System;
using UnityEngine;

public enum LODLevel : byte
{
    Near = 0,
    Mid = 1,
    Far = 2
}

public struct LODSettings
{
    public int stride;          // voxel step size (1 = full res)
    public bool detailedBlocks; // whether to assign detailed block types
    public Material material;   // material for mesh
    
    [NonSerialized] public int densityCount; // Computed at runtime based on chunkSize/stride
}
