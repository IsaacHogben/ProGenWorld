using System;
using Unity.Collections;
using Unity.Mathematics;

public struct MeshData
{
    [NonSerialized] public int3 coord;
    [NonSerialized] public NativeList<float3> vertices;
    [NonSerialized] public NativeList<int> triangles;
    [NonSerialized] public NativeList<float3> normals;
    [NonSerialized] public NativeList<float4> colors;
    [NonSerialized] public NativeList<float2> UV0s;
}
