using System;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Moves an axis aligned box through the voxel grid: per-axis sweeps, automatic step-up onto
/// ledges and ground snapping when walking down. Pure logic - the solid query is injected, so
/// VoxelBody drives it from the block data and tests can drive it from a fake grid.
/// One world unit = one block.
/// </summary>
public class VoxelBoxMover
{
    public const float Skin = 0.001f;

    public Vector3 Size = new(0.7f, 3f, 0.7f);
    /// <summary>Box center relative to the pivot.</summary>
    public Vector3 Center = new(0f, 1.5f, 0f);
    /// <summary>Ledges up to this height are climbed automatically while grounded (0 = off).</summary>
    public float StepHeight;
    /// <summary>While grounded, stay on the ground when it drops by up to this much (0 = off).</summary>
    public float GroundSnapDistance;

    readonly Func<int3, bool> isSolid;

    public VoxelBoxMover(Func<int3, bool> isSolid)
    {
        this.isSolid = isSolid;
    }

    public struct Result
    {
        public bool Grounded;
        /// <summary>Height climbed by an automatic step this move (0 if none).</summary>
        public float StepUp;
        /// <summary>Height dropped by ground snapping this move (0 if none).</summary>
        public float SnapDown;
        public bool HitWall;
    }

    public void GetBox(Vector3 pos, out Vector3 min, out Vector3 max)
    {
        Vector3 c = pos + Center;
        Vector3 h = Size * 0.5f;
        min = c - h;
        max = c + h;
    }

    public bool Overlaps(Vector3 pos)
    {
        GetBox(pos, out var min, out var max);
        min += Vector3.one * Skin;
        max -= Vector3.one * Skin;

        int3 a = (int3)math.floor((float3)min);
        int3 b = (int3)math.ceil((float3)max) - 1;
        for (int x = a.x; x <= b.x; x++)
        for (int y = a.y; y <= b.y; y++)
        for (int z = a.z; z <= b.z; z++)
            if (isSolid(new int3(x, y, z)))
                return true;
        return false;
    }

    /// <summary>If the box is inside blocks (terrain loaded around it, block placed on it), lift it out.</summary>
    public bool Depenetrate(ref Vector3 pos)
    {
        if (!Overlaps(pos))
            return false;

        GetBox(pos, out var min, out _);
        float baseY = Mathf.Floor(min.y);
        for (int i = 1; i <= Mathf.CeilToInt(Size.y) + 2; i++)
        {
            Vector3 candidate = pos;
            candidate.y += (baseY + i) - min.y + Skin;
            if (!Overlaps(candidate))
            {
                pos = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Moves the box by <paramref name="delta"/>. Velocity components are zeroed on the axes that hit
    /// something. <paramref name="wasGrounded"/> is the grounded state from the previous move and
    /// enables stepping and ground snapping on the first frame after a small drop.
    /// </summary>
    public Result Move(ref Vector3 pos, ref Vector3 velocity, Vector3 delta, bool wasGrounded)
    {
        var r = new Result();

        // Vertical first so walking over flat ground never snags on the floor.
        Sweep(ref pos, 1, delta.y, out bool hitY);
        if (hitY)
        {
            if (delta.y < 0f) r.Grounded = true;
            velocity.y = 0f;
        }

        Vector3 start = pos;
        float dx = Sweep(ref pos, 0, delta.x, out bool hitX);
        float dz = Sweep(ref pos, 2, delta.z, out bool hitZ);

        bool canStep = StepHeight > 0f && velocity.y <= 0f && (r.Grounded || wasGrounded);
        if ((hitX || hitZ) && canStep && TryStep(start, delta, dx, dz, out Vector3 stepped, out bool stepHitX, out bool stepHitZ))
        {
            r.StepUp = stepped.y - start.y;
            r.Grounded = true;
            velocity.y = 0f;
            pos = stepped;
            hitX = stepHitX;
            hitZ = stepHitZ;
        }

        if (hitX) velocity.x = 0f;
        if (hitZ) velocity.z = 0f;
        r.HitWall = hitX || hitZ;

        // Walking off a small drop: stick to the ground instead of starting a fall.
        if (!r.Grounded && wasGrounded && GroundSnapDistance > 0f && velocity.y <= 0f)
        {
            Vector3 snapped = pos;
            float moved = Sweep(ref snapped, 1, -GroundSnapDistance, out bool landed);
            if (landed)
            {
                pos = snapped;
                r.SnapDown = -moved;
                r.Grounded = true;
                velocity.y = 0f;
            }
        }

        if (!r.Grounded && velocity.y <= 0f)
            r.Grounded = Overlaps(pos + Vector3.down * (Skin * 4f));

        return r;
    }

    // Classic stair step: up, across, back down. Accepted only if it lands on something and gets
    // further than the blocked flat move did.
    bool TryStep(Vector3 start, Vector3 delta, float flatX, float flatZ, out Vector3 pos, out bool hitX, out bool hitZ)
    {
        pos = start;
        hitX = hitZ = false;

        float up = Sweep(ref pos, 1, StepHeight, out _);
        if (up <= Skin * 2f)
            return false;

        float sx = Sweep(ref pos, 0, delta.x, out hitX);
        float sz = Sweep(ref pos, 2, delta.z, out hitZ);
        Sweep(ref pos, 1, -up, out bool landed);

        float rise = pos.y - start.y;
        float flatProgress = flatX * flatX + flatZ * flatZ;
        float stepProgress = sx * sx + sz * sz;
        return landed && rise > 0.01f && stepProgress > flatProgress + 1e-8f;
    }

    /// <summary>Sweeps the box along one axis, stopping at the first solid voxel layer. Returns the distance moved.</summary>
    public float Sweep(ref Vector3 pos, int axis, float amount, out bool hit)
    {
        hit = false;
        if (Mathf.Abs(amount) < 1e-7f)
            return 0f;

        GetBox(pos, out var min, out var max);

        int a1 = (axis + 1) % 3;
        int a2 = (axis + 2) % 3;
        int lo1 = Mathf.FloorToInt(min[a1] + Skin), hi1 = Mathf.CeilToInt(max[a1] - Skin) - 1;
        int lo2 = Mathf.FloorToInt(min[a2] + Skin), hi2 = Mathf.CeilToInt(max[a2] - Skin) - 1;

        float allowed = amount;

        if (amount > 0f)
        {
            float edge = max[axis];
            float target = edge + amount;
            for (int layer = Mathf.CeilToInt(edge - Skin); layer < target; layer++)
            {
                if (LayerSolid(axis, layer, a1, lo1, hi1, a2, lo2, hi2))
                {
                    allowed = Mathf.Max(0f, layer - edge - Skin);
                    hit = true;
                    break;
                }
            }
        }
        else
        {
            float edge = min[axis];
            float target = edge + amount;
            for (int layer = Mathf.FloorToInt(edge + Skin) - 1; layer + 1 > target; layer--)
            {
                if (LayerSolid(axis, layer, a1, lo1, hi1, a2, lo2, hi2))
                {
                    allowed = Mathf.Min(0f, (layer + 1) - edge + Skin);
                    hit = true;
                    break;
                }
            }
        }

        pos[axis] += allowed;
        return allowed;
    }

    bool LayerSolid(int axis, int layer, int a1, int lo1, int hi1, int a2, int lo2, int hi2)
    {
        int3 p = int3.zero;
        p[axis] = layer;
        for (int i = lo1; i <= hi1; i++)
        {
            p[a1] = i;
            for (int j = lo2; j <= hi2; j++)
            {
                p[a2] = j;
                if (isSolid(p))
                    return true;
            }
        }
        return false;
    }
}
