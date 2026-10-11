using System;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Character physics against the voxel grid, without engine calls (VoxelCharacterMotor drives it,
/// tests drive it from a fake grid).
///
/// Only the torso is a collision box: it starts <see cref="Settings.stepClearance"/> above the feet.
/// Terrain lower than that is not a wall - the body rides over it on a critically damped spring
/// toward the ground height, which is averaged with look-ahead samples so block stairs and voxel
/// hills turn into smooth ramps. Drops larger than <see cref="Settings.maxStepDown"/> become falls,
/// and reaching a ledge in the air lands on it (the spring lifts the body up, like a mantle).
/// </summary>
public class VoxelCharacterMotorCore
{
    [Serializable]
    public struct Settings
    {
        [Tooltip("Half width of the collision box")]
        public float radius;
        [Tooltip("Height from the feet to the top of the head")]
        public float height;
        [Tooltip("Terrain lower than this above the feet is walked over; anything higher is a wall. 1.5 = one block steps, two block walls")]
        public float stepClearance;
        [Tooltip("Half width of the area under the feet that holds the body up")]
        public float supportRadius;
        [Tooltip("Largest drop walked down without falling (1 = one block)")]
        public float maxStepDown;
        [Tooltip("Spring frequency (Hz) when rising to higher ground")]
        public float riseFrequency;
        [Tooltip("Spring frequency (Hz) when lowering to lower ground")]
        public float lowerFrequency;
        public float maxRiseSpeed;
        public float maxLowerSpeed;
        [Tooltip("Seconds of movement looked ahead when following the ground. Turns block steps into ramps")]
        public float groundLookahead;
        [Tooltip("Shortest look-ahead distance, used when moving slowly")]
        public float minLookaheadDistance;
        [Tooltip("How far the feet may end up below the ground while the body catches up")]
        public float maxSink;
        public float gravity;
        public float terminalVelocity;

        public static Settings Default => new Settings
        {
            radius = 0.35f,
            height = 3f,
            stepClearance = 1.5f,
            supportRadius = 0.25f,
            maxStepDown = 1.2f,
            riseFrequency = 2.6f,
            lowerFrequency = 2.4f,
            maxRiseSpeed = 9f,
            maxLowerSpeed = 9f,
            groundLookahead = 0.22f,
            minLookaheadDistance = 0.6f,
            maxSink = 0.75f,
            gravity = -9.81f,
            terminalVelocity = 40f,
        };
    }

    public struct StepResult
    {
        public bool Landed;
        public float ImpactSpeed;
        public bool Jumped;
        public bool LeftGround;
    }

    const float Skin = VoxelBoxMover.Skin;
    const float JumpLockTime = 0.12f;

    public Settings settings = Settings.Default;

    public Vector3 Position;
    /// <summary>Horizontal velocity actually moved with and the vertical velocity.</summary>
    public Vector3 Velocity;
    public bool Grounded;
    /// <summary>Height of the ground under the feet (last known while airborne).</summary>
    public float GroundHeight;
    public float AirTime;
    public bool HitWall;

    readonly Func<int3, bool> isSolid;
    readonly VoxelBoxMover torso;
    float jumpLock;
    float lastTarget;
    float targetRate;
    bool hasTarget;

    public VoxelCharacterMotorCore(Func<int3, bool> isSolid)
    {
        this.isSolid = isSolid;
        torso = new VoxelBoxMover(isSolid);
    }

    /// <summary>Places the body, standing on whatever ground is under it.</summary>
    public void Teleport(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.zero;
        Grounded = false;
        AirTime = 0f;
        GroundHeight = position.y;
    }

    void ConfigureTorso()
    {
        float h = Mathf.Max(0.1f, settings.height - settings.stepClearance);
        torso.Size = new Vector3(settings.radius * 2f, h, settings.radius * 2f);
        torso.Center = new Vector3(0f, settings.stepClearance + h * 0.5f, 0f);
        torso.StepHeight = 0f;
        torso.GroundSnapDistance = 0f;
    }

    /// <summary>
    /// Advances the body one fixed step. <paramref name="jumpSpeed"/> > 0 starts a jump (the caller
    /// decides whether jumping is allowed).
    /// </summary>
    public StepResult Step(Vector3 horizontalVelocity, float jumpSpeed, float dt)
    {
        var result = new StepResult();
        ConfigureTorso();

        // Blocks appeared inside the torso (terrain loaded around it, block placed): lift out.
        torso.Depenetrate(ref Position);

        // Can't move forward faster than the body can climb: slow down once the feet are buried
        // deeper than riding a block staircase normally needs.
        Vector3 hv = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        if (Grounded && settings.maxSink > 0f && hasTarget && lastTarget > Position.y)
        {
            float buried = (GroundHeight - Position.y) / settings.maxSink;
            if (buried > 0.75f)
                hv *= Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((buried - 0.75f) * 4f));
        }

        // Horizontal: only the torso collides; the legs walk over anything below it.
        bool hitX = MoveHorizontal(0, hv.x * dt);
        bool hitZ = MoveHorizontal(2, hv.z * dt);
        if (hitX) hv.x = 0f;
        if (hitZ) hv.z = 0f;
        HitWall = hitX || hitZ;
        Velocity.x = hv.x;
        Velocity.z = hv.z;

        if (jumpSpeed > 0f)
        {
            Velocity.y = jumpSpeed;
            jumpLock = JumpLockTime;
            if (Grounded) result.LeftGround = true;
            Grounded = false;
            result.Jumped = true;
        }

        if (Grounded)
            StepGrounded(hv, dt, ref result);
        else
            StepAirborne(dt, ref result);

        if (Grounded) AirTime = 0f;
        else AirTime += dt;

        return result;
    }

    void StepGrounded(Vector3 hv, float dt, ref StepResult result)
    {
        float y = Position.y;
        float top = y + settings.stepClearance;

        // Walked off something taller than a step: fall.
        if (!TryGetSupport(Position, top, GroundHeight - settings.maxStepDown, out float support))
        {
            Grounded = false;
            result.LeftGround = true;
            StepAirborne(dt, ref result);
            return;
        }
        GroundHeight = support;

        float target = GroundTarget(support, hv, top);

        // Rate the target is moving (climbing a ramp): fed forward so the spring doesn't lag on slopes.
        float rate = hasTarget ? (target - lastTarget) / dt : 0f;
        rate = Mathf.Clamp(rate, -settings.maxLowerSpeed, settings.maxRiseSpeed);
        targetRate = Mathf.Lerp(targetRate, rate, 1f - Mathf.Exp(-dt / 0.1f));
        lastTarget = target;
        hasTarget = true;

        // Critically damped spring toward the ground height.
        float error = target - y;
        float omega = 2f * Mathf.PI * (error > 0f ? settings.riseFrequency : settings.lowerFrequency);
        float vy = Velocity.y + (omega * omega * error + 2f * omega * (targetRate - Velocity.y)) * dt;
        vy = Mathf.Clamp(vy, -settings.maxLowerSpeed, settings.maxRiseSpeed);

        float newY = Mathf.Max(y + vy * dt, support - settings.maxSink);
        torso.Sweep(ref Position, 1, newY - y, out bool hitY);
        Velocity.y = hitY ? 0f : (Position.y - y) / dt;

        // Moving down faster than the body can follow the ground (sliding down stairs at speed):
        // fall instead of floating above it.
        if (Position.y - support > settings.maxStepDown + 0.3f)
        {
            Grounded = false;
            result.LeftGround = true;
        }
    }

    void StepAirborne(float dt, ref StepResult result)
    {
        Velocity.y = Mathf.Max(Velocity.y + settings.gravity * dt, -settings.terminalVelocity);

        float y = Position.y;
        torso.Sweep(ref Position, 1, Velocity.y * dt, out bool hitY);
        if (hitY && Velocity.y > 0f)
            Velocity.y = 0f;

        if (jumpLock > 0f)
        {
            jumpLock -= dt;
            return;
        }
        if (Velocity.y > 0f)
            return;

        // Feet reached ground (anything up to the torso counts, so ledges can be caught).
        float top = Position.y + settings.stepClearance;
        if (TryGetSupport(Position, top, Mathf.Min(y, Position.y) - 0.05f, out float support) && support >= Position.y - 0.05f)
        {
            result.Landed = true;
            result.ImpactSpeed = -Velocity.y;
            Grounded = true;
            GroundHeight = support;
            Velocity.y = 0f;
            hasTarget = false;
            targetRate = 0f;
            if (Position.y < support - settings.maxSink)
                Position.y = support - settings.maxSink;
        }
    }

    // Sweeps the torso along one axis, refusing to walk onto ground that leaves no headroom.
    bool MoveHorizontal(int axis, float amount)
    {
        Vector3 before = Position;
        torso.Sweep(ref Position, axis, amount, out bool hit);
        if (!HasHeadroom(Position))
        {
            Position = before;
            return true;
        }
        return hit;
    }

    // True if the body could stand up on the ground under this point.
    bool HasHeadroom(Vector3 p)
    {
        if (!TryGetSupport(p, p.y + settings.stepClearance, p.y - settings.maxStepDown, out float s) || s <= p.y + 0.01f)
            return true;
        return !torso.Overlaps(new Vector3(p.x, s, p.z));
    }

    // Ground height averaged along the path ahead, ignoring walls and cliffs. Looking at least a
    // little way ahead even when slow means block steps always become ramps.
    float GroundTarget(float support, Vector3 hv, float top)
    {
        float speed = hv.magnitude;
        if (speed < 0.1f || settings.groundLookahead <= 0f)
            return support;

        float reach = Mathf.Clamp(speed * settings.groundLookahead, settings.minLookaheadDistance, 2f);
        Vector3 dir = hv / speed;

        float sum = support;
        int n = 1;
        for (int i = 1; i <= 2; i++)
        {
            Vector3 p = Position + dir * (reach * i * 0.5f);
            if (TryGetSupport(p, top, support - settings.maxStepDown, out float ahead))
            {
                sum += ahead;
                n++;
            }
        }
        return sum / n;
    }

    /// <summary>Highest standing surface under the support footprint, between minY and fromY. Wall columns are ignored.</summary>
    public bool TryGetSupport(Vector3 pos, float fromY, float minY, out float support)
    {
        support = float.NegativeInfinity;
        float r = settings.supportRadius;
        int x0 = Mathf.FloorToInt(pos.x - r + Skin), x1 = Mathf.FloorToInt(pos.x + r - Skin);
        int z0 = Mathf.FloorToInt(pos.z - r + Skin), z1 = Mathf.FloorToInt(pos.z + r - Skin);

        bool found = false;
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (TryGetColumnSurface(x, z, fromY, minY, out float s) && s > support)
            {
                support = s;
                found = true;
            }
        }
        return found;
    }

    bool TryGetColumnSurface(int x, int z, float fromY, float minY, out float surfaceY)
    {
        surfaceY = float.NegativeInfinity;
        int top = Mathf.FloorToInt(fromY);
        int bottom = Mathf.FloorToInt(minY);
        if (isSolid(new int3(x, top, z)))
            return false;
        for (int y = top - 1; y >= bottom; y--)
        {
            if (isSolid(new int3(x, y, z)))
            {
                surfaceY = y + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rise per metre of the ground ahead in <paramref name="direction"/> (1 = a 45 degree block
    /// staircase, negative = downhill). 0 when airborne or nothing is measurable.
    /// </summary>
    public float MeasureSlope(Vector3 direction)
    {
        direction.y = 0f;
        if (!Grounded || direction.sqrMagnitude < 1e-4f)
            return 0f;
        direction.Normalize();

        float top = GroundHeight + settings.stepClearance;
        float sum = 0f;
        int n = 0;
        for (int i = 1; i <= 2; i++)
        {
            float d = 0.8f * i;
            Vector3 p = new Vector3(Position.x, GroundHeight, Position.z) + direction * d;
            // Allow rises steeper than 45 degrees so staircases measure as slopes, not walls.
            if (TryGetSupport(p, top + d, GroundHeight - settings.maxStepDown * 2f, out float s))
            {
                sum += (s - GroundHeight) / d;
                n++;
            }
            else
            {
                break; // a wall or a cliff: stop measuring past it
            }
        }
        return n > 0 ? sum / n : 0f;
    }
}
