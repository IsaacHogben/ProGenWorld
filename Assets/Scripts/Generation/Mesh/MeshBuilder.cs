using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;

public static class MeshBuilder
{
    static Dictionary<int3, GameObject> chunkObjects = new();

    public static void BuildChunk(ChunkData data, int chunkSize, float voxelScale)
    {
        // Greedy mesher
        List<Vector3> verts = new();
        List<int> tris = new();

        for (int x = 0; x < chunkSize; x++)
            for (int y = 0; y < chunkSize; y++)
                for (int z = 0; z < chunkSize; z++)
                {
                    int i = x + chunkSize * (y + chunkSize * z);
                    float d = data.density[i];

                    if (d < 0) continue; // inside solid voxel

                    // Add a cube (basic placeholder)
                    AddCube(verts, tris, new Vector3(x, y, z) * voxelScale, voxelScale);
                }

        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();

        var go = GetOrCreateChunkObject(data.coord, chunkSize, voxelScale);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        go.GetComponent<MeshCollider>().sharedMesh = mesh;

        if (data.density.IsCreated)
            data.density.Dispose();
    }

    static void AddCube(List<Vector3> verts, List<int> tris, Vector3 pos, float size)
    {
        int start = verts.Count;

        Vector3[] cubeVerts = {
            pos + new Vector3(0,0,0), pos + new Vector3(1,0,0), pos + new Vector3(1,1,0), pos + new Vector3(0,1,0),
            pos + new Vector3(0,0,1), pos + new Vector3(1,0,1), pos + new Vector3(1,1,1), pos + new Vector3(0,1,1)
        };
        verts.AddRange(cubeVerts);

        int[] faces = {
            0,2,1, 0,3,2,  // front
            5,6,7, 5,7,4,  // back
            4,7,3, 4,3,0,  // left
            1,2,6, 1,6,5,  // right
            3,7,6, 3,6,2,  // top
            4,0,1, 4,1,5   // bottom
        };
        for (int i = 0; i < faces.Length; i++) tris.Add(start + faces[i]);
    }

    static GameObject GetOrCreateChunkObject(int3 coord, int chunkSize, float scale)
    {
        if (!chunkObjects.TryGetValue(coord, out GameObject go))
        {
            go = new GameObject($"Chunk {coord}");
            go.transform.position = new Vector3(coord.x * chunkSize, coord.y * chunkSize, coord.z * chunkSize) * scale;
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            go.AddComponent<MeshCollider>();
            chunkObjects[coord] = go;
        }
        return go;
    }
}
