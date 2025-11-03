using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

[ExecuteAlways]
public class ChunkDebugVisualizer : MonoBehaviour
{
    public ChunkManager chunkManager;
    public float faceOffset = 0.51f;  // how far the face quads extend from chunk bounds
    public bool showOpenFaces = true;
    public bool showChunkBounds = false;
    public bool showDeferredChunks = false;
    public Color boundsColor = new(0.4f, 0.4f, 0.4f, 0.4f);
    public Color faceColor = new(1f, 0.3f, 0.3f, 0.7f);
    public Color deferredChunkColor = new(1f, 0.3f, 0.3f, 0.7f);

    void OnDrawGizmos()
    {
        if (chunkManager == null)
            return;

        Gizmos.matrix = Matrix4x4.identity;

        if (showDeferredChunks)
        foreach (var c in chunkManager.GetDeferredFrontier())
        {
            int3 coord = c;
            Vector3 pos = chunkManager.ChunkToWorld(coord);
            float size = chunkManager.chunkSize;

            Gizmos.color = deferredChunkColor;
            Gizmos.DrawWireCube(pos + Vector3.one * (size / 2f), Vector3.one * size);
        }
    }

    void DrawOpenFaces(Vector3 basePos, float size, OpenFaces faces)
    {
        Vector3 center = basePos + Vector3.one * (size / 2f);
        Vector3 half = Vector3.one * (size / 2f);

        if (faces.HasFlag(OpenFaces.PosX))
            DrawFace(center + Vector3.right * (half.x + faceOffset), Vector3.right, Vector3.up, Vector3.forward, size);
        if (faces.HasFlag(OpenFaces.NegX))
            DrawFace(center + Vector3.left * (half.x + faceOffset), Vector3.left, Vector3.up, Vector3.forward, size);
        if (faces.HasFlag(OpenFaces.PosY))
            DrawFace(center + Vector3.up * (half.y + faceOffset), Vector3.up, Vector3.right, Vector3.forward, size);
        if (faces.HasFlag(OpenFaces.NegY))
            DrawFace(center + Vector3.down * (half.y + faceOffset), Vector3.down, Vector3.right, Vector3.forward, size);
        if (faces.HasFlag(OpenFaces.PosZ))
            DrawFace(center + Vector3.forward * (half.z + faceOffset), Vector3.forward, Vector3.right, Vector3.up, size);
        if (faces.HasFlag(OpenFaces.NegZ))
            DrawFace(center + Vector3.back * (half.z + faceOffset), Vector3.back, Vector3.right, Vector3.up, size);
    }

    void DrawFace(Vector3 center, Vector3 normal, Vector3 axisA, Vector3 axisB, float size)
    {
        Vector3 a = axisA * (size / 2f);
        Vector3 b = axisB * (size / 2f);
        Vector3 v0 = center - a - b;
        Vector3 v1 = center + a - b;
        Vector3 v2 = center + a + b;
        Vector3 v3 = center - a + b;

        Gizmos.DrawLine(v0, v1);
        Gizmos.DrawLine(v1, v2);
        Gizmos.DrawLine(v2, v3);
        Gizmos.DrawLine(v3, v0);
    }
}
