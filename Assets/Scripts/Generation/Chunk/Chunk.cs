using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public class Chunk : MonoBehaviour
{
    public Material chunkMaterial;

    public void ApplyMesh(MeshData meshData, Stack<Mesh> meshPool)
    {
        // Ensure required components
        var mf = GetComponent<MeshFilter>(); if (mf == null) mf = gameObject.AddComponent<MeshFilter>(); 
        var mr = GetComponent<MeshRenderer>(); if (mr == null) mr = gameObject.AddComponent<MeshRenderer>(); 
        var mc = GetComponent<MeshCollider>(); if (mc == null) mc = gameObject.AddComponent<MeshCollider>();

        if (mr.sharedMaterial == null)
            mr.sharedMaterial = chunkMaterial;

        // Get or reuse mesh
        Mesh mesh;
        if (mf.sharedMesh == null)
        {
            mesh = meshPool.Count > 0 ? meshPool.Pop() : new Mesh { indexFormat = IndexFormat.UInt32 };
            mf.sharedMesh = mesh;
        }
        else
        {
            mesh = mf.sharedMesh;
            mesh.Clear();
        }

        // Reset collider before reassigning
        //mc.sharedMesh = null;

        // Allocate writable MeshData and copy directly
        var meshArray = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData dst = meshArray[0];

        dst.SetVertexBufferParams(meshData.vertices.Length,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 3));

        dst.SetIndexBufferParams(meshData.triangles.Length, IndexFormat.UInt32);

        // Copy vertex data to writable buffers
        meshData.vertices.AsArray().CopyTo(dst.GetVertexData<float3>(0));
        meshData.normals.AsArray().CopyTo(dst.GetVertexData<float3>(1));
        meshData.colors.AsArray().CopyTo(dst.GetVertexData<float4>(2));
        meshData.UV0s.AsArray().CopyTo(dst.GetVertexData<float2>(3));
        meshData.triangles.AsArray().CopyTo(dst.GetIndexData<int>());

        dst.subMeshCount = 1;
        dst.SetSubMesh(0, new SubMeshDescriptor(0, meshData.triangles.Length));

        // Apply to mesh (auto-disposes MeshDataArray)
        Mesh.ApplyAndDisposeWritableMeshData(meshArray, mesh);
        mesh.RecalculateBounds();

        // Assign to collider last (this binds physics)
        mc.sharedMesh = mesh;
    }

    public void Release(Stack<Mesh> meshPool)
    {
        var mf = GetComponent<MeshFilter>();
        var mc = GetComponent<MeshCollider>();

        if (mf && mf.sharedMesh)
        {
            // Return to pool or destroy
            mf.sharedMesh.Clear();
            meshPool.Push(mf.sharedMesh);
            mf.sharedMesh = null;
        }

        if (mc)
            mc.sharedMesh = null;

        gameObject.SetActive(false);
    }
}
