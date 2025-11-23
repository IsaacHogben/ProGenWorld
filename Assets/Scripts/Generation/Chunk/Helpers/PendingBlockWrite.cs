using Unity.Mathematics;
using UnityEngine;

public struct PendingBlockWrite
{
    public int3 targetChunk;
    public int3 localPos;     // x,y,z inside the target chunk
    public byte blockId;      // block type being placed
    public PendingWriteMode mode;
    public bool isMirror;
}

public enum PendingWriteMode : byte
{
    Replace,       // always overwrite
    ReplaceAir,    // only write if block is air (0)
    ReplaceSoft,   // write if block is air or "soft" (leaves, plants)
}