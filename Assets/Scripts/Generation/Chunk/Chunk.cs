using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using System.Collections.Generic;

public class Chunk : MonoBehaviour
{
    public Material chunkMaterial;
    public LODLevel lod;

    // Tune hard cap on pooled meshes per world
    const int MaxMeshPoolSizePerWorld = 102;

    public void ApplyMesh(MeshData meshData, Stack<Mesh> meshPool)
    {
        // Ensure components
        var mf = GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();

        var mr = GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();

        if (chunkMaterial != null && mr.sharedMaterial != chunkMaterial)
            mr.sharedMaterial = chunkMaterial;

        // Get or reuse mesh
        Mesh mesh = mf.sharedMesh;

        if (mesh == null)
        {
            // Try from pool first
            if (meshPool != null && meshPool.Count > 0)
            {
                mesh = meshPool.Pop();
            }

            if (mesh == null)
            {
                mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
            }

            mf.sharedMesh = mesh;
        }

        // Allocate writable MeshData and copy directly
        var meshArray = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData dst = meshArray[0];

        // Vertex layout
        dst.SetVertexBufferParams(
            meshData.vertices.Length,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 3)
        );

        // Index buffer
        dst.SetIndexBufferParams(meshData.triangles.Length, IndexFormat.UInt32);

        // Copy vertex/index data
        meshData.vertices.AsArray().CopyTo(dst.GetVertexData<float3>(0));
        meshData.normals.AsArray().CopyTo(dst.GetVertexData<float3>(1));
        meshData.colors.AsArray().CopyTo(dst.GetVertexData<float4>(2));
        meshData.UV0s.AsArray().CopyTo(dst.GetVertexData<float2>(3));
        meshData.triangles.AsArray().CopyTo(dst.GetIndexData<int>());

        dst.subMeshCount = 1;
        dst.SetSubMesh(0, new SubMeshDescriptor(0, meshData.triangles.Length));

        // Apply to mesh (also disposes MeshDataArray)
        Mesh.ApplyAndDisposeWritableMeshData(meshArray, mesh);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false); // keep CPU copy for collider & future rewrites

        // Collider handling:
        if (meshData.stride == 0)
        {
            var mc = GetComponent<MeshCollider>();
            if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }
        else
        {
            // Ensure we do not keep old collider meshes alive on non-near LODs
            var mc = GetComponent<MeshCollider>();
            if (mc != null)
                mc.sharedMesh = null;
        }
    }
    public void Release(Stack<Mesh> meshPool)
    {
        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();
        var mc = GetComponent<MeshCollider>();

        // Detach collider mesh
        if (mc != null)
        {
            mc.sharedMesh = null;

            // mc.enabled = false;
        }

        // Return mesh to pool or destroy if pool is full
        if (mf != null)
        {
            var mesh = mf.sharedMesh;
            if (mesh != null)
            {
                mf.sharedMesh = null;

                if (meshPool != null && meshPool.Count < MaxMeshPoolSizePerWorld)
                {
                    // No need to Clear() – ApplyAndDisposeWritableMeshData overwrites the content next time.
                    // Keep it dynamic for frequent updates.
                    mesh.MarkDynamic();
                    meshPool.Push(mesh);
                }
                else
                {
                    // Hard cap: if we have enough pooled meshes, destroy extras
                    Destroy(mesh);
                }
            }
        }
        gameObject.SetActive(false);
    }
}
