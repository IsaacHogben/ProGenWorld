using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>Anything that can hand out chunk block arrays (implemented by ChunkManager).</summary>
public interface IVoxelWorldSource
{
    int ChunkSize { get; }
    NativeArray<BlockDatabase.BlockInfoUnmanaged> BlockDatabase { get; }

    /// <summary>Returns the chunk's block array when it exists and is safe to read on the main thread.</summary>
    bool TryGetChunkBlocks(int3 chunkCoord, out NativeArray<byte> blocks);
}

public struct VoxelRaycastHit
{
    public Vector3 point;
    public Vector3 normal;
    public float distance;
    public Vector3Int voxel;
    public byte blockId;
}

/// <summary>
/// Collision queries that read the generated block data directly - no colliders, no baked meshes.
/// One world unit = one block.
/// </summary>
public static class VoxelPhysics
{
    public static IVoxelWorldSource Source { get; set; }

    public static bool IsReady => Source != null;

    // ----------------------------------------------------------------------------------
    // Block access
    // ----------------------------------------------------------------------------------

    public static int3 WorldToChunk(int3 voxel, int chunkSize)
    {
        return new int3(
            (int)math.floor(voxel.x / (float)chunkSize),
            (int)math.floor(voxel.y / (float)chunkSize),
            (int)math.floor(voxel.z / (float)chunkSize));
    }

    /// <summary>Returns false when the chunk holding this voxel is not loaded (or busy).</summary>
    public static bool TryGetBlock(int3 voxel, out byte blockId)
    {
        blockId = 0;
        if (Source == null)
            return false;

        int size = Source.ChunkSize;
        int3 chunk = WorldToChunk(voxel, size);
        if (!Source.TryGetChunkBlocks(chunk, out var blocks))
            return false;

        int3 local = voxel - chunk * size;
        int s = size + 1;
        blockId = blocks[local.x + local.y * s + local.z * s * s];
        return true;
    }

    public static bool IsChunkLoaded(Vector3 position)
    {
        if (Source == null)
            return false;
        int3 voxel = (int3)math.floor((float3)position);
        return Source.TryGetChunkBlocks(WorldToChunk(voxel, Source.ChunkSize), out _);
    }

    /// <summary>True if the voxel blocks movement. Unloaded voxels use <paramref name="unloadedIsSolid"/>.</summary>
    public static bool IsSolid(int3 voxel, bool unloadedIsSolid = false)
    {
        if (!TryGetBlock(voxel, out byte id))
            return unloadedIsSolid;
        var db = Source.BlockDatabase;
        return id < db.Length && db[id].hasCollision;
    }

    /// <summary>True if the voxel is water.</summary>
    public static bool IsWater(int3 voxel)
    {
        return TryGetBlock(voxel, out byte id) && id == (byte)BlockType.Water;
    }

    /// <summary>True if any solid voxel intersects the box.</summary>
    public static bool OverlapBox(Vector3 min, Vector3 max, bool unloadedIsSolid = false)
    {
        int3 a = (int3)math.floor((float3)min);
        int3 b = (int3)math.ceil((float3)max) - 1;
        for (int x = a.x; x <= b.x; x++)
        for (int y = a.y; y <= b.y; y++)
        for (int z = a.z; z <= b.z; z++)
            if (IsSolid(new int3(x, y, z), unloadedIsSolid))
                return true;
        return false;
    }

    /// <summary>
    /// Finds the standing surface in one block column: the top of the first solid voxel at or
    /// below <paramref name="fromY"/>, no lower than <paramref name="minY"/>. Returns false when
    /// the voxel containing fromY is itself solid (the column is a wall at that height) or when no
    /// surface is found.
    /// </summary>
    public static bool TryGetColumnSurface(int x, int z, float fromY, float minY, out float surfaceY, bool unloadedIsSolid = false)
    {
        surfaceY = float.NegativeInfinity;
        int top = (int)math.floor(fromY);
        int bottom = (int)math.floor(minY);

        if (IsSolid(new int3(x, top, z), unloadedIsSolid))
            return false;

        for (int y = top - 1; y >= bottom; y--)
        {
            if (IsSolid(new int3(x, y, z), unloadedIsSolid))
            {
                surfaceY = y + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>Surface height under a world point, searching from <paramref name="up"/> above it to <paramref name="down"/> below.</summary>
    public static bool TryGetGroundHeight(Vector3 point, float up, float down, out float groundY)
    {
        return TryGetColumnSurface((int)math.floor(point.x), (int)math.floor(point.z), point.y + up, point.y - down, out groundY);
    }

    // ----------------------------------------------------------------------------------
    // Raycasts (voxel DDA)
    // ----------------------------------------------------------------------------------

    public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        return Raycast(origin, direction, out _, maxDistance);
    }

    /// <summary>
    /// Casts a ray against solid voxels. Like Physics.Raycast, a ray that starts inside a solid
    /// voxel does not report that voxel.
    /// </summary>
    public static bool Raycast(Vector3 origin, Vector3 direction, out VoxelRaycastHit hit, float maxDistance)
    {
        hit = default;
        if (Source == null || direction.sqrMagnitude < 1e-12f)
            return false;

        float3 o = origin;
        float3 d = math.normalize((float3)direction);

        int3 voxel = (int3)math.floor(o);
        int3 step = (int3)math.sign(d);
        float3 tDelta = math.abs(1f / d);
        float3 next = (float3)voxel + math.max((float3)step, 0f);
        float3 tMax = math.select((next - o) / d, float.PositiveInfinity, step == 0);
        tDelta = math.select(tDelta, float.PositiveInfinity, step == 0);

        float t = 0f;
        int axis = -1;

        while (t <= maxDistance)
        {
            if (axis >= 0 && IsSolid(voxel))
            {
                float3 n = float3.zero;
                n[axis] = -step[axis];

                hit.distance = t;
                hit.point = o + d * t;
                hit.normal = n;
                hit.voxel = new Vector3Int(voxel.x, voxel.y, voxel.z);
                TryGetBlock(voxel, out hit.blockId);
                return true;
            }

            if (tMax.x < tMax.y && tMax.x < tMax.z) axis = 0;
            else if (tMax.y < tMax.z) axis = 1;
            else axis = 2;

            t = tMax[axis];
            tMax[axis] += tDelta[axis];
            voxel[axis] += step[axis];
        }

        return false;
    }
}
