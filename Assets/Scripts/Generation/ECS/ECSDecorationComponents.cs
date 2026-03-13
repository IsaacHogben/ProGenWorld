using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

public struct ECSDecoration : IComponentData { }

public struct ECSDecorationData : IComponentData
{
    public byte Category;
    public byte DecorationType;
    public byte BiomeIndex;
}

public struct ECSChunkOwner : IComponentData
{
    public int3 ChunkCoord;
}

public struct ECSSpawnRequest : IComponentData
{
    public int3 ChunkCoord;
    public float3 WorldOffset;
    public BlobAssetReference<ECSSpawnBlob> Points;
}

public struct ECSSpawnBlob
{
    public BlobArray<ECSSpawnPoint> Points;
}
