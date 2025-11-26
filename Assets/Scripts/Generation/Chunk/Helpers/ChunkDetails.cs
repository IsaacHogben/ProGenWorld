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
    public int meshRes;         // resolution that mesh samples block array
    public int sampleRes;       // step size of noise sample - influences size of array
    public int blockSize;       // size of mesh block outputs
    public bool detailedBlocks; // whether to assign detailed block types
    public Material material;   // material for mesh
    
    [NonSerialized] public int densityCount; // Computed at runtime based on chunkSize/stride
}
