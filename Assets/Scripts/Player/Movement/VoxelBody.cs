using UnityEngine;

/// <summary>
/// Kinematic box body that collides directly against the voxel block data (see VoxelPhysics).
/// Replaces Rigidbody + MeshCollider terrain collision: no physics meshes are ever baked.
///
/// The pivot is the transform position; the box is described by <see cref="center"/> and
/// <see cref="size"/> relative to it. Runs before other scripts in FixedUpdate so controllers
/// read the post-simulation position and set the velocity used on the next step.
///
/// Steps and ground snaps move the simulated body instantly; the rendered transform eases
/// into place over a few frames (<see cref="stepSmoothing"/>), so cameras and models parented
/// to this object see a smooth climb rather than a pop. During FixedUpdate the transform holds
/// the exact simulated position.
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

    [Header("Stepping")]
    [Tooltip("Ledges up to this height are climbed automatically while grounded (0 = off).")]
    public float stepHeight = 0f;
    [Tooltip("While grounded, stay on the ground when it drops by up to this much instead of falling (0 = off).")]
    public float groundSnapDistance = 0f;
    [Tooltip("How quickly the rendered position catches up after a step or snap (1/s, 0 = instant).")]
    public float stepSmoothing = 14f;
    [Tooltip("Largest distance the rendered position may lag behind the simulation after steps.")]
    public float maxStepSmoothingOffset = 1f;

    [Header("State")]
    public Vector3 linearVelocity;

    public bool IsGrounded { get; private set; }
    /// <summary>False while the chunk under the body has no block data yet (body is frozen).</summary>
    public bool IsSimulating { get; private set; }
    /// <summary>True if the last fixed step was blocked by a wall horizontally.</summary>
    public bool HitWall { get; private set; }
    /// <summary>Height climbed by an automatic step on the last fixed step (0 if none).</summary>
    public float LastStepUp { get; private set; }
    /// <summary>Vertical offset of the rendered position from the simulation (negative right after stepping up).</summary>
    public float VisualOffset => visualOffsetY;

    VoxelBoxMover mover;
    Vector3 simPosition;
    Vector3 prevPosition;
    Vector3 lastWrittenPosition;
    float visualOffsetY;

    public Vector3 position
    {
        get => simPosition;
        set
        {
            simPosition = value;
            prevPosition = value;
            visualOffsetY = 0f;
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

        mover = new VoxelBoxMover(v => VoxelPhysics.IsSolid(v, unloadedIsSolid));
        simPosition = prevPosition = lastWrittenPosition = transform.position;
    }

    /// <summary>Moves the body without breaking interpolation (use for small scripted moves).</summary>
    public void MovePosition(Vector3 target)
    {
        simPosition = target;
        WriteTransform(target);
    }

    void FixedUpdate()
    {
        SyncFromTransform();
        prevPosition = simPosition;
        LastStepUp = 0f;

        IsSimulating = VoxelPhysics.IsReady && VoxelPhysics.IsChunkLoaded(simPosition + center);
        if (!IsSimulating)
        {
            WriteTransform(simPosition);
            return;
        }

        float dt = Time.fixedDeltaTime;
        if (useGravity)
            linearVelocity += Physics.gravity * (gravityScale * dt);

        mover.Size = size;
        mover.Center = center;
        mover.StepHeight = stepHeight;
        mover.GroundSnapDistance = groundSnapDistance;

        if (mover.Depenetrate(ref simPosition))
        {
            prevPosition = simPosition;
            if (linearVelocity.y < 0f) linearVelocity.y = 0f;
        }

        var result = mover.Move(ref simPosition, ref linearVelocity, linearVelocity * dt, IsGrounded);
        IsGrounded = result.Grounded;
        HitWall = result.HitWall;
        LastStepUp = result.StepUp;

        // Steps and snaps are discontinuities: keep them out of the interpolation and ease them in instead.
        float jump = result.StepUp - result.SnapDown;
        if (jump != 0f)
        {
            prevPosition.y += jump;
            if (stepSmoothing > 0f)
                visualOffsetY = Mathf.Clamp(visualOffsetY - jump, -maxStepSmoothingOffset, maxStepSmoothingOffset);
        }

        WriteTransform(simPosition);
    }

    void Update()
    {
        SyncFromTransform();
        if (!IsSimulating)
            return;

        if (stepSmoothing > 0f)
        {
            visualOffsetY *= Mathf.Exp(-stepSmoothing * Time.deltaTime);
            if (Mathf.Abs(visualOffsetY) < 1e-4f) visualOffsetY = 0f;
        }
        else
        {
            visualOffsetY = 0f;
        }

        Vector3 p = simPosition;
        if (interpolate)
        {
            float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            p = Vector3.Lerp(prevPosition, simPosition, alpha);
        }
        p.y += visualOffsetY;
        WriteTransform(p);
    }

    // Someone else moved the transform (teleport, editor drag...) - adopt it.
    void SyncFromTransform()
    {
        Vector3 p = transform.position;
        if ((p - lastWrittenPosition).sqrMagnitude > 1e-10f)
        {
            simPosition = p;
            prevPosition = p;
            lastWrittenPosition = p;
            visualOffsetY = 0f;
        }
    }

    void WriteTransform(Vector3 p)
    {
        transform.position = p;
        lastWrittenPosition = p;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = IsGrounded ? Color.green : Color.yellow;
        Vector3 p = Application.isPlaying ? simPosition : transform.position;
        Gizmos.DrawWireCube(p + center, size);
    }
}
