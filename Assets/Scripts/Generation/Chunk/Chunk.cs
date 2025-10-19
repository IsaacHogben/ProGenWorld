using Unity.Collections;
using UnityEngine;

public class Chunk : MonoBehaviour
{
    public Material chunkMaterial;

    public void ApplyMesh(MeshData meshData)
    {
        // Ensure required components exist
        var mf = GetComponent<MeshFilter>();
        if (mf == null)
            mf = gameObject.AddComponent<MeshFilter>();

        var mr = GetComponent<MeshRenderer>();
        if (mr == null)
            mr = gameObject.AddComponent<MeshRenderer>();

        if (mr.sharedMaterial == null)
            mr.sharedMaterial = chunkMaterial;

        // Build mesh
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(meshData.vertices.AsArray());
        mesh.SetTriangles(meshData.triangles.AsArray().ToArray(), 0);
        mesh.RecalculateNormals();

        mf.sharedMesh = mesh;

        // Dispose native containers
        if (meshData.vertices.IsCreated) meshData.vertices.Dispose();
        if (meshData.triangles.IsCreated) meshData.triangles.Dispose();
        if (meshData.normals.IsCreated) meshData.normals.Dispose();
    }
}
