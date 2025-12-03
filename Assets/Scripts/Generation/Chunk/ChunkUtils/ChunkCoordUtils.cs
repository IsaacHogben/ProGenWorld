using Unity.Mathematics;
using UnityEngine;

public static class ChunkCoordUtils
{
    public static Vector3 ChunkToWorld(int3 coord, int chunkSize)
    {
        return new Vector3(
            coord.x * chunkSize,
            coord.y * chunkSize,
            coord.z * chunkSize
        );
    }

    public static Vector3 ChunkToWorldCenter(int3 coord, int chunkSize)
    {
        float off = chunkSize * 0.5f;
        return new Vector3(
            coord.x * chunkSize + off,
            coord.y * chunkSize + off,
            coord.z * chunkSize + off
        );
    }

    public static int3 WorldToChunkCoord(Vector3 pos, int chunkSize)
    {
        return new int3(
            Mathf.FloorToInt(pos.x / chunkSize),
            Mathf.FloorToInt(pos.y / chunkSize),
            Mathf.FloorToInt(pos.z / chunkSize)
        );
    }

    public static float GetChunkDistance(
        int3 chunkCoord,
        Vector3 worldPos,
        int chunkSize,
        RenderShape renderShape)
    {
        Vector3 chunkCenter = ChunkToWorldCenter(chunkCoord, chunkSize);

        switch (renderShape)
        {
            case RenderShape.Cylinder:
                {
                    float dx = chunkCenter.x - worldPos.x;
                    float dz = chunkCenter.z - worldPos.z;
                    float distSq = dx * dx + dz * dz;
                    float dist = math.sqrt(distSq);
                    return dist / chunkSize;
                }

            case RenderShape.Sphere:
            default:
                {
                    float dx = chunkCenter.x - worldPos.x;
                    float dy = chunkCenter.y - worldPos.y;
                    float dz = chunkCenter.z - worldPos.z;
                    float distSq = dx * dx + dy * dy + dz * dz;
                    float dist = math.sqrt(distSq);
                    return dist / chunkSize;
                }
        }
    }
}
