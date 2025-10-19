using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct VoxelChunkData : IComponentData
{
    public int3 ChunkCoord;
}

public struct VoxelBuffer : IBufferElementData
{
    public byte Value; // 0 = empty, >0 = solid, could encode material ID later
}
