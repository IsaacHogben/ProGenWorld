using Unity.Mathematics;

public struct ECSSpawnPoint
{
    public float3 position;       // local to chunk
    public byte category;       // DecorationCategory
    public byte decorationType; // DecorationType.Vegetation, etc.
    public byte biomeIndex;     // for tint/variation later
    public byte flags;          // reserved
}