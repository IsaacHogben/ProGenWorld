using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Kinematic box body that collides directly against the voxel block data (see VoxelPhysics).
/// Replaces Rigidbody + MeshCollider terrain collision: no physics meshes are ever baked.
///
/// The pivot is the transform position; the box is described by <see cref="center"/> and
/// <see cref="size"/> relative to it. Runs before other scripts in FixedUpdate so controllers
/// read the post-simulation position and set the velocity used on the next step.
/// </summary>
[DefaultExecutionOrder(-50)]
public class VoxelBody : MonoBehaviour
{
    [Header("Shape (axis aligned box, relative to the pivot)")]
    public Vector3 size = new(0.7f, 3f, 0.7f);
    public Vector3 center = new(0f, 1.5f, 0f);
    [Tooltip("Copy the box from a CapsuleCollider on this object when the body starts.")]
    public bool fitToCapsuleCollider = true;

    [Header("Simulation")]
    public bool useGravity = true;
    public float gravityScale = 1f;
    [Tooltip("Smooth the rendered position between fixed steps (like Rigidbody interpolation).")]
    public bool interpolate = true;
    [Tooltip("Treat chunks that are not generated yet as solid walls.")]
    public bool unloadedIsSolid = false;

    [Header("State")]
    public Vector3 linearVelocity;

    public bool IsGrounded { get; private set; }
    /// <summary>False while the chunk under the body has no block data yet (body is frozen).</summary>
    public bool IsSimulating { get; private set; }

    const float Skin = 0.001f;

    Vector3 simPosition;
    Vector3 prevPosition;
    Vector3 lastWrittenPosition;

    public Vector3 position
    {
        get => simPosition;
        set
        {
            simPosition = value;
            prevPosition = value;
            WriteTransform(value);
        }
    }

    void Awake()
    {
        if (fitToCapsuleCollider && TryGetComponent(out CapsuleCollider capsule))
        {
            float d = capsule.radius * 2f;
            size = capsule.direction switch
            {
                0 => new Vector3(capsule.height, d, d),
                2 => new Vector3(d, d, capsule.height),
                _ => new Vector3(d, capsule.height, d),
            };
            center = capsule.center;
        }

        // A Rigidbody left on the object would fight this body.
        if (TryGetComponent(out Rigidbody rb))
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        simPosition = prevPosition = lastWrittenPosition = transform.position;
    }

    /// <summary>Moves the body without breaking interpolation (use for small scripted moves like stepping).</summary>
    public void MovePosition(Vector3 target)
    {
        simPosition = target;
        WriteTransform(target);
    }

    void FixedUpdate()
    {
        SyncFromTransform();
        prevPosition = simPosition;

        IsSimulating = VoxelPhysics.IsReady && VoxelPhysics.IsChunkLoaded(simPosition + center);
        if (!IsSimulating)
        {
            WriteTransform(simPosition);
            return;
        }

        float dt = Time.fixedDeltaTime;
        if (useGravity)
            linearVelocity += Physics.gravity * (gravityScale * dt);

        Depenetrate();

        Vector3 delta = linearVelocity * dt;
        IsGrounded = false;

        // Vertical first so walking over flat ground never snags on the floor.
        MoveAxis(1, delta.y);
        MoveAxis(0, delta.x);
        MoveAxis(2, delta.z);

        if (!IsGrounded && linearVelocity.y <= 0f)
            IsGrounded = Overlaps(simPosition + Vector3.down * (Skin * 4f));

        WriteTransform(simPosition);
    }

    void Update()
    {
        SyncFromTransform();
        if (!interpolate || !IsSimulating)
            return;

        float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
        WriteTransform(Vector3.Lerp(prevPosition, simPosition, alpha));
    }

    // Someone else moved the transform (teleport, controller step...) - adopt it.
    void SyncFromTransform()
    {
        Vector3 p = transform.position;
        if ((p - lastWrittenPosition).sqrMagnitude > 1e-10f)
        {
            simPosition = p;
            prevPosition = p;
            lastWrittenPosition = p;
        }
    }

    void WriteTransform(Vector3 p)
    {
        transform.position = p;
        lastWrittenPosition = p;
    }

    void GetBox(Vector3 pos, out Vector3 min, out Vector3 max)
    {
        Vector3 c = pos + center;
        Vector3 h = size * 0.5f;
        min = c - h;
        max = c + h;
    }

    bool Overlaps(Vector3 pos)
    {
        GetBox(pos, out var min, out var max);
        return VoxelPhysics.OverlapBox(min + Vector3.one * Skin, max - Vector3.one * Skin, unloadedIsSolid);
    }

    // If the body ended up inside blocks (terrain loaded around it, block placed on it), lift it out.
    void Depenetrate()
    {
        if (!Overlaps(simPosition))
            return;

        GetBox(simPosition, out var min, out _);
        float baseY = Mathf.Floor(min.y);
        for (int i = 1; i <= Mathf.CeilToInt(size.y) + 2; i++)
        {
            Vector3 candidate = simPosition;
            candidate.y += (baseY + i) - min.y + Skin;
            if (!Overlaps(candidate))
            {
                simPosition = candidate;
                if (linearVelocity.y < 0f) linearVelocity.y = 0f;
                return;
            }
        }
    }

    // Sweeps the box along one axis, stopping at the first solid voxel layer.
    void MoveAxis(int axis, float amount)
    {
        if (Mathf.Abs(amount) < 1e-7f)
            return;

        GetBox(simPosition, out var min, out var max);

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
                    break;
                }
            }
        }

        simPosition[axis] += allowed;

        if (!Mathf.Approximately(allowed, amount))
        {
            if (axis == 1 && amount < 0f)
                IsGrounded = true;

            Vector3 v = linearVelocity;
            v[axis] = 0f;
            linearVelocity = v;
        }
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
                if (VoxelPhysics.IsSolid(p, unloadedIsSolid))
                    return true;
            }
        }
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = IsGrounded ? Color.green : Color.yellow;
        Gizmos.DrawWireCube(transform.position + center, size);
    }
}
