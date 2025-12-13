using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using System.Collections.Generic;

public class Chunk : MonoBehaviour
{
    public Material chunkMaterial;
    public Material waterMaterial;

    // separate filter & renderer for water
    private MeshFilter waterMF;
    private MeshRenderer waterMR;

    public LODLevel lod;

    // Tune hard cap on pooled meshes per world
    const int MaxMeshPoolSizePerWorld = 102;

    public void ApplyMesh(MeshData meshData, Stack<Mesh> meshPool)
    {
        Profiler.StartUpload();

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
        if (lod == LODLevel.Near)
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

        Profiler.EndUpload();
    }
    public void ApplyWaterMesh(MeshData meshData, Stack<Mesh> meshPool)
    {
        Profiler.StartUpload();

        if (waterMaterial == null)
        {
            Debug.LogError("Chunk is missing waterMaterial!");
            Profiler.EndUpload();
            return;
        }
        // Find child call waterGO

        GameObject waterGO;

        Transform waterTransform = transform.Find("Water");
        if (waterTransform != null)
        {
            // Found existing water object - get references
            waterGO = waterTransform.gameObject;
            waterMF = waterGO.GetComponent<MeshFilter>();
            waterMR = waterGO.GetComponent<MeshRenderer>();

            // Add missing components if needed
            if (waterMF == null) waterMF = waterGO.AddComponent<MeshFilter>();
            if (waterMR == null) waterMR = waterGO.AddComponent<MeshRenderer>();
        }
        else
        {
            // Create new water object
            waterGO = new GameObject("Water");
            waterGO.transform.SetParent(transform);
            waterGO.transform.localPosition = Vector3.zero;
            waterMF = waterGO.AddComponent<MeshFilter>();
            waterMR = waterGO.AddComponent<MeshRenderer>();
        }

        waterMR.sharedMaterial = waterMaterial;

        // --- Get or reuse mesh ---
        Mesh mesh = waterMF.sharedMesh;

        if (mesh == null)
        {
            if (meshPool != null && meshPool.Count > 0)
                mesh = meshPool.Pop();
            else
                mesh = new Mesh { indexFormat = IndexFormat.UInt32 };

            mesh.MarkDynamic();
            waterMF.sharedMesh = mesh;
        }

        // --- Build mesh buffers ---
        var meshArray = Mesh.AllocateWritableMeshData(1);
        Mesh.MeshData dst = meshArray[0];

        dst.SetVertexBufferParams(
            meshData.vertices.Length,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 1),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, stream: 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: 3)
        );

        dst.SetIndexBufferParams(meshData.triangles.Length, IndexFormat.UInt32);

        meshData.vertices.AsArray().CopyTo(dst.GetVertexData<float3>(0));
        meshData.normals.AsArray().CopyTo(dst.GetVertexData<float3>(1));
        meshData.colors.AsArray().CopyTo(dst.GetVertexData<float4>(2));
        meshData.UV0s.AsArray().CopyTo(dst.GetVertexData<float2>(3));
        meshData.triangles.AsArray().CopyTo(dst.GetIndexData<int>());

        dst.subMeshCount = 1;
        dst.SetSubMesh(0, new SubMeshDescriptor(0, meshData.triangles.Length));

        Mesh.ApplyAndDisposeWritableMeshData(meshArray, mesh);
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);

        //waterMC.sharedMesh = mesh;

        Profiler.EndUpload();
    }

    public void ClearMesh(Stack<Mesh> meshPool)
    {
        var mf = GetComponent<MeshFilter>();
        var mc = GetComponent<MeshCollider>();

        if (mf != null && mf.sharedMesh != null)
        {
            meshPool.Push(mf.sharedMesh);
            mf.sharedMesh = null;
        }

        if (mc != null)
            mc.sharedMesh = null;
        ClearWaterMesh(meshPool);
    }
    public void ClearWaterMesh(Stack<Mesh> meshPool)
    {
        if (waterMF != null && waterMF.sharedMesh != null)
        {
            meshPool.Push(waterMF.sharedMesh);
            waterMF.sharedMesh = null;
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
                    // Keep it dynamic for frequent updates.
                    mesh.MarkDynamic();
                    meshPool.Push(mesh);
                }
                else
                {
                    // if we have enough pooled meshes, destroy extras
                    Destroy(mesh);
                }
            }
        }
        // --- water mesh cleanup ---
        if (waterMF != null)
        {
            var mesh = waterMF.sharedMesh;
            if (mesh != null)
            {
                waterMF.sharedMesh = null;

                if (meshPool != null && meshPool.Count < MaxMeshPoolSizePerWorld)
                {
                    mesh.MarkDynamic();
                    meshPool.Push(mesh);
                }
                else
                {
                    Destroy(mesh);
                }
            }
        }
        gameObject.SetActive(false);
    }
}