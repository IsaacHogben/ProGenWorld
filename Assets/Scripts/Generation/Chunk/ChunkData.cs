using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

public struct ChunkData
{
    public int3 coord;
    public NativeArray<float> density;
    public JobHandle jobHandle;
}
