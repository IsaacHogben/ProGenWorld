using System;
using UnityEngine;

/// <summary>
/// Character body for the voxel world (see <see cref="VoxelCharacterMotorCore"/> for how it moves).
/// The pivot is at the feet. A controller sets <see cref="HorizontalVelocity"/> and calls
/// <see cref="Jump"/> before this runs in FixedUpdate; the rendered transform is interpolated
/// between fixed steps.
/// </summary>
[DefaultExecutionOrder(-50)]
public class VoxelCharacterMotor : MonoBehaviour
{
    [SerializeField] private VoxelCharacterMotorCore.Settings settings = VoxelCharacterMotorCore.Settings.Default;
    [Tooltip("Gravity multiplier (uses Physics.gravity)")]
    [SerializeField] private float gravityScale = 1f;
    [Tooltip("Treat chunks that are not generated yet as solid")]
    [SerializeField] private bool unloadedIsSolid = false;
    [Tooltip("Smooth the rendered position between fixed steps")]
    [SerializeField] private bool interpolate = true;

    /// <summary>Desired horizontal velocity for the next fixed step (y ignored).</summary>
    public Vector3 HorizontalVelocity { get; set; }
    /// <summary>While swimming: +1 swims up, -1 dives.</summary>
    public float SwimVertical { get; set; }
    /// <summary>Dev fly mode: no collision or gravity, moves with <see cref="FlyVelocity"/>.</summary>
    public bool NoClip { get; set; }
    public Vector3 FlyVelocity { get; set; }

    public Vector3 Velocity => core.Velocity;
    public bool IsGrounded => core.Grounded;
    public float GroundHeight => core.GroundHeight;
    public float AirTime => core.AirTime;
    public bool HitWall => core.HitWall;
    public bool IsSwimming => core.Swimming;
    /// <summary>Height of the water surface above the feet (0 or less out of water).</summary>
    public float WaterDepth => core.WaterDepth;
    /// <summary>False while the chunk around the body has no block data yet (body is frozen).</summary>
    public bool IsSimulating { get; private set; }
    /// <summary>Simulated position (the transform holds the interpolated one during rendering).</summary>
    public Vector3 Position => core.Position;
    public VoxelCharacterMotorCore.Settings Settings => settings;

    /// <summary>Fired on landing with the downward impact speed.</summary>
    public event Action<float> Landed;
    public event Action Jumped;
    /// <summary>Started swimming (fell or walked into deep water). Not a landing: no impact.</summary>
    public event Action EnteredWater;

    VoxelCharacterMotorCore core;
    float pendingJump;
    Vector3 prevPosition;
    Vector3 lastWrittenPosition;

    void Awake()
    {
        core = new VoxelCharacterMotorCore(v => VoxelPhysics.IsSolid(v, unloadedIsSolid)) { IsWater = VoxelPhysics.IsWater };
        core.Teleport(transform.position);
        prevPosition = lastWrittenPosition = transform.position;

        // Leftover Unity physics on the object would fight this body.
        if (TryGetComponent(out Rigidbody rb))
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
    }

    /// <summary>Starts a jump on the next fixed step. The caller decides whether jumping is allowed.</summary>
    public void Jump(float speed)
    {
        pendingJump = Mathf.Max(pendingJump, speed);
    }

    /// <summary>Scales upward velocity (cutting a jump short when the button is released).</summary>
    public void ScaleUpwardVelocity(float multiplier)
    {
        if (!core.Grounded && core.Velocity.y > 0f)
            core.Velocity.y *= multiplier;
    }

    public void Teleport(Vector3 position)
    {
        core.Teleport(position);
        prevPosition = position;
        WriteTransform(position);
    }

    /// <summary>Rise per metre of the ground ahead (1 = 45 degree block stairs, negative = downhill).</summary>
    public float MeasureSlope(Vector3 direction) => core.MeasureSlope(direction);

    void FixedUpdate()
    {
        SyncFromTransform();
        prevPosition = core.Position;

        if (NoClip)
        {
            IsSimulating = true;
            core.Position += FlyVelocity * Time.fixedDeltaTime;
            core.Velocity = FlyVelocity;
            core.Grounded = false;
            core.Swimming = false;
            core.AirTime = 0f;
            WriteTransform(core.Position);
            return;
        }

        IsSimulating = VoxelPhysics.IsReady && VoxelPhysics.IsChunkLoaded(core.Position + Vector3.up);
        if (!IsSimulating)
        {
            WriteTransform(core.Position);
            return;
        }

        core.settings = settings;
        core.settings.gravity = Physics.gravity.y * gravityScale;

        var result = core.Step(HorizontalVelocity, pendingJump, Time.fixedDeltaTime, SwimVertical);
        pendingJump = 0f;

        WriteTransform(core.Position);

        if (result.Jumped) Jumped?.Invoke();
        if (result.Landed) Landed?.Invoke(result.ImpactSpeed);
        if (result.EnteredWater) EnteredWater?.Invoke();
    }

    void Update()
    {
        SyncFromTransform();
        if (!IsSimulating || !interpolate)
            return;

        float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
        WriteTransform(Vector3.Lerp(prevPosition, core.Position, alpha));
    }

    // Someone else moved the transform (teleport, editor drag) - adopt it.
    void SyncFromTransform()
    {
        Vector3 p = transform.position;
        if ((p - lastWrittenPosition).sqrMagnitude > 1e-10f)
        {
            core.Teleport(p);
            prevPosition = p;
            lastWrittenPosition = p;
        }
    }

    void WriteTransform(Vector3 p)
    {
        transform.position = p;
        lastWrittenPosition = p;
    }

    void OnDrawGizmosSelected()
    {
        var s = Application.isPlaying ? core.settings : settings;
        Vector3 feet = Application.isPlaying ? core.Position : transform.position;
        float h = s.height - s.stepClearance;

        Gizmos.color = Application.isPlaying && core.Grounded ? Color.green : Color.yellow;
        Gizmos.DrawWireCube(feet + Vector3.up * (s.stepClearance + h * 0.5f), new Vector3(s.radius * 2f, h, s.radius * 2f));
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(feet + Vector3.up * (s.stepClearance * 0.5f), new Vector3(s.supportRadius * 2f, s.stepClearance, s.supportRadius * 2f));
    }
}
