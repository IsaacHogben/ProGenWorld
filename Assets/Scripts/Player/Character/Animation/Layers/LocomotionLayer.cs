using UnityEngine;

/// <summary>
/// Legs and pelvis: a phase-driven gait whose cadence and stride follow the body's speed. Feet
/// stay locked to the ground while planted and swing to landing spots predicted from the
/// velocity; swing arcs lift over the voxel edges in their path, rising early onto a step and
/// dropping late off one. The pelvis bobs with the steps, lowers when a foot is on lower ground
/// (stairs), and compresses on landing. Standing still, the feet shuffle back under the body.
/// </summary>
public class LocomotionLayer : CharacterAnimLayer
{
    [Header("Gait Timing")]
    [Tooltip("Steps per second when barely moving")]
    [SerializeField] private float minCadence = 1.8f;
    [Tooltip("Steps per second at walk speed")]
    [SerializeField] private float walkCadence = 3.6f;
    [Tooltip("Steps per second at run speed")]
    [SerializeField] private float runCadence = 4.7f;
    [Tooltip("Steps per second when shuffling the feet back under the body while standing")]
    [SerializeField] private float settleCadence = 3.4f;
    [Tooltip("Fraction of the cycle a foot is on the ground when walking")]
    [Range(0.3f, 0.8f)] [SerializeField] private float walkStanceFraction = 0.56f;
    [Tooltip("Fraction of the cycle a foot is on the ground when running (below 0.5 = both feet leave the ground)")]
    [Range(0.2f, 0.8f)] [SerializeField] private float runStanceFraction = 0.34f;
    [Tooltip("Extra cadence on slopes (per unit of slope): shorter, quicker steps on stairs and hills")]
    [SerializeField] private float slopeCadence = 0.7f;

    [Header("Foot Placement")]
    [Tooltip("Extra stance width added to the model's rest pose")]
    [SerializeField] private float extraStanceWidth = 0.02f;
    [Tooltip("Furthest ahead of the hip a foot lands")]
    [SerializeField] private float maxStrideLead = 0.9f;
    [Tooltip("Standing still, a foot further than this from its rest spot steps back under the body")]
    [SerializeField] private float settleDistance = 0.14f;
    [Tooltip("Standing still, a foot turned further than this (degrees) steps to realign")]
    [SerializeField] private float settleAngle = 35f;
    [Tooltip("Deepest drop a foot reaches down to (should match the body's max step down)")]
    [SerializeField] private float maxFootDrop = 1.25f;
    [Tooltip("Raise or lower the feet relative to the ground (if they float or sink)")]
    [SerializeField] private float footHeightAdjust = 0f;

    [Header("Swing")]
    [SerializeField] private float walkLift = 0.25f;
    [SerializeField] private float runLift = 0.5f;
    [Tooltip("Gap kept between the swinging foot and block edges in its path")]
    [SerializeField] private float obstacleClearance = 0.15f;
    [Tooltip("Toe pitch through the swing (degrees)")]
    [SerializeField] private float walkToePitch = 15f;
    [SerializeField] private float runToePitch = 30f;

    [Header("Pelvis")]
    [SerializeField] private float walkBob = 0.035f;
    [SerializeField] private float runBob = 0.07f;
    [Tooltip("Side to side shift over the standing foot when walking")]
    [SerializeField] private float walkSway = 0.03f;
    [Tooltip("Fraction of the leg length used before the pelvis lowers to keep a foot reachable")]
    [Range(0.8f, 1f)] [SerializeField] private float legReach = 0.96f;
    [Tooltip("Pelvis height follows the ground under the supporting feet; this blends toward the body's own (smoothed, look-ahead) height instead. 0 = feet only")]
    [Range(0f, 1f)] [SerializeField] private float bodyHeightFollow = 0.25f;
    [Tooltip("Smoothing time of the pelvis following the ground under the feet (seconds)")]
    [SerializeField] private float groundFollowSmoothTime = 0.12f;
    [Tooltip("Smoothing time of the pelvis lowering to keep the feet reachable (seconds)")]
    [SerializeField] private float pelvisSmoothTime = 0.09f;
    [SerializeField] private float maxPelvisDrop = 1.1f;
    [Tooltip("Knee bend kept while walking / running (pelvis lowered by this much)")]
    [SerializeField] private float walkCrouch = 0.05f;
    [SerializeField] private float runCrouch = 0.14f;
    [Tooltip("How far the pelvis lowers when fully crouched")]
    [SerializeField] private float crouchDepth = 0.5f;

    [Header("Landing")]
    [Tooltip("Pelvis compression per m/s of landing speed")]
    [SerializeField] private float landingCompression = 0.035f;
    [SerializeField] private float maxLandingCompression = 0.55f;
    [Tooltip("Suspension frequency (Hz) of the landing bounce")]
    [SerializeField] private float landingFrequency = 2.8f;
    [Range(0.1f, 1f)] [SerializeField] private float landingDamping = 0.5f;
    [Tooltip("Seconds for the feet to reach the ground after landing")]
    [SerializeField] private float landingFootTime = 0.07f;

    [Header("Air")]
    [Tooltip("Seconds in the air before the air pose starts (ignores tiny hops)")]
    [SerializeField] private float airPoseDelay = 0.1f;
    [Tooltip("How high the feet tuck when rising")]
    [SerializeField] private float airTuck = 0.6f;
    [Tooltip("Front/back split of the feet in the air")]
    [SerializeField] private float airStride = 0.35f;
    [SerializeField] private float airBlendSpeed = 9f;

    class Foot
    {
        public Vector3 rest;         // facing frame, relative to the root (y = ground level)
        public bool swinging;
        public Vector3 plant;        // world, while planted
        public float plantYaw;
        public Vector3 liftPos;      // world, where the swing started
        public float liftYaw;
        public Vector3 pos;
        public float yaw;
        public float pitch;
        public Vector3 airLocal;     // facing frame, while airborne
        public Vector3 landFrom;
        public float landT = 1f;
        public float prevP;
        public float swingStart;
        public float p;
        public Vector3 target;       // world, where the current swing lands
    }

    readonly Foot[] feet = { new Foot(), new Foot() };

    float phase;
    float duty = 0.58f;
    bool inAir;
    bool jumped;
    float pelvisDrop;
    float pelvisDropVelocity;
    float pelvisGround;
    float lastPelvisRootY;
    float pelvisGroundVelocity;
    float compression;
    float compressionVelocity;
    float runBlend;
    float gaitWeight;
    float airBlend;
    float sole;
    Vector3 lastRoot;
    bool initialized;

    public float Phase => phase;

    public override void Initialize(CharacterAnimator animator, CharacterAnimContext context)
    {
        base.Initialize(animator, context);

        var rig = context.Rig;
        sole = rig.SoleHeight;
        for (int i = 0; i < 2; i++)
        {
            Vector3 r = rig.Legs[i].Valid ? rig.Legs[i].RestEndPosition : new Vector3(i == 0 ? -0.2f : 0.2f, sole, 0f);
            r.x += (i == 0 ? -1f : 1f) * extraStanceWidth;
            r.y = 0f;
            feet[i].rest = r;
        }

        if (context.Source != null)
        {
            context.Source.Landed -= OnLanded;
            context.Source.Jumped -= OnJumped;
            context.Source.Landed += OnLanded;
            context.Source.Jumped += OnJumped;
        }
    }

    void OnDestroy()
    {
        if (Animator != null && Animator.Source != null)
        {
            Animator.Source.Landed -= OnLanded;
            Animator.Source.Jumped -= OnJumped;
        }
    }

    void OnLanded(float impact)
    {
        jumped = false;
        float kick = Mathf.Min(impact * landingCompression, maxLandingCompression);
        compressionVelocity -= kick * 2f * Mathf.PI * landingFrequency;
    }

    void OnJumped()
    {
        jumped = true;
        // Push off: a quick extension of the legs.
        compressionVelocity += 0.6f;
    }

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;

        if (!initialized || (ctx.Root - lastRoot).sqrMagnitude > 25f)
            ResetFeet(ctx);
        lastRoot = ctx.Root;

        Vector3 velocity = s.Velocity;
        velocity.y = 0f;
        float speed = velocity.magnitude;

        float walkSpeed = Mathf.Max(0.5f, s.WalkSpeed);
        runBlend = Mathf.Lerp(runBlend, Mathf.InverseLerp(walkSpeed, Mathf.Max(walkSpeed + 0.1f, s.RunSpeed), speed), Damp(8f, dt));
        gaitWeight = Mathf.Lerp(gaitWeight, Mathf.Clamp01(speed / walkSpeed), Damp(10f, dt));

        bool air = jumped || (!s.Grounded && s.AirTime > airPoseDelay);
        if (air)
        {
            if (!inAir) EnterAir(ctx);
            UpdateAir(ctx);
        }
        else
        {
            if (inAir) Land(ctx, velocity);
            UpdateGait(ctx, velocity, speed);
        }
        airBlend = Mathf.MoveTowards(airBlend, inAir ? 1f : 0f, dt * 6f);

        UpdatePelvis(pose, ctx);

        for (int i = 0; i < 2; i++)
        {
            var f = feet[i];
            pose.Feet[i] = new FootPose
            {
                Position = f.pos + Vector3.up * (sole + footHeightAdjust),
                Yaw = f.yaw,
                Pitch = f.pitch,
                Weight = weight,
            };
        }

        pose.GaitPhase = phase;
        pose.GaitWeight = inAir ? 0f : gaitWeight;
        pose.RunBlend = runBlend;
        pose.Airborne = airBlend;
    }

    // ----------------------------------------------------------------------------------
    // Ground
    // ----------------------------------------------------------------------------------

    // Feet positions are kept at ground level; the sole height is added when posing.
    bool TryGround(Vector3 point, float referenceY, out float y)
    {
        return VoxelPhysics.TryGetGroundHeight(new Vector3(point.x, referenceY, point.z), 1.5f, 2.5f, out y);
    }

    Vector3 RestTarget(Foot f, CharacterAnimContext ctx) => ctx.Root + ctx.Facing * f.rest;

    Vector3 OnGround(Vector3 p, CharacterAnimContext ctx, Vector3 fallback)
    {
        if (TryStandable(p, ctx, out float y))
            return new Vector3(p.x, y, p.z);
        if (TryStandable(fallback, ctx, out y))
            return new Vector3(fallback.x, y, fallback.z);
        return new Vector3(fallback.x, ctx.State.GroundHeight, fallback.z);
    }

    // Ground a foot can be put on: not a wall, and not further down than the body can step
    // (beyond that the body falls, so the foot shouldn't reach for it).
    bool TryStandable(Vector3 p, CharacterAnimContext ctx, out float y)
    {
        return TryGround(p, ctx.State.GroundHeight, out y) && y >= ctx.State.GroundHeight - maxFootDrop;
    }

    void ResetFeet(CharacterAnimContext ctx)
    {
        initialized = true;
        foreach (var f in feet)
        {
            Vector3 rest = RestTarget(f, ctx);
            f.pos = f.plant = OnGround(rest, ctx, rest);
            f.yaw = f.plantYaw = ctx.State.FacingYaw;
            f.swinging = false;
            f.pitch = 0f;
            f.landT = 1f;
        }
        phase = 0.05f;
        pelvisGround = lastPelvisRootY = ctx.Root.y;
        pelvisGroundVelocity = 0f;
        feet[0].prevP = 0.05f;
        feet[1].prevP = 0.55f;
        pelvisDrop = 0f;
    }

    // ----------------------------------------------------------------------------------
    // Gait
    // ----------------------------------------------------------------------------------

    void UpdateGait(CharacterAnimContext ctx, Vector3 velocity, float speed)
    {
        var s = ctx.State;
        float dt = ctx.DeltaTime;
        float walkSpeed = Mathf.Max(0.5f, s.WalkSpeed);

        float cadence = speed < walkSpeed
            ? Mathf.Lerp(minCadence, walkCadence, speed / walkSpeed)
            : Mathf.Lerp(walkCadence, runCadence, runBlend);
        duty = Mathf.Lerp(duty, Mathf.Lerp(walkStanceFraction, runStanceFraction, runBlend), Damp(4f, dt));
        cadence *= 1f + slopeCadence * Mathf.Min(Mathf.Abs(s.Slope), 1.5f);
        float cycleRate = cadence * 0.5f;

        // Standing still: keep stepping only until the feet are back under the body.
        bool moving = speed >= 0.2f;
        if (!moving)
            cycleRate = NeedsSettle(ctx) ? settleCadence * 0.5f : 0f;

        phase = Mathf.Repeat(phase + cycleRate * dt, 1f);

        for (int i = 0; i < 2; i++)
        {
            var f = feet[i];
            float p = Mathf.Repeat(phase + 0.5f * i, 1f);
            f.p = p;
            bool wrapped = p < f.prevP - 0.5f;

            if (f.swinging)
            {
                Vector3 target = SwingTarget(f, ctx, velocity, wrapped ? 1f : p, cycleRate);
                if (wrapped)
                {
                    // Touchdown.
                    f.swinging = false;
                    f.plant = target;
                    f.plantYaw = s.FacingYaw;
                    f.pos = target;
                    f.yaw = f.plantYaw;
                }
                else
                {
                    UpdateSwing(f, ctx, target, Mathf.InverseLerp(f.swingStart, 1f, p));
                }
            }
            else if (p >= duty && (moving || FootNeedsSettle(f, ctx)))
            {
                // Lift off. The swing runs to the end of this foot's cycle, so later changes in
                // timing can't cut it short.
                f.swinging = true;
                f.swingStart = p;
                f.liftPos = f.pos;
                f.liftYaw = f.yaw;
                f.landT = 1f;
            }

            if (!f.swinging)
            {
                // Planted: locked to the world.
                if (f.landT < 1f)
                {
                    f.landT = Mathf.Min(1f, f.landT + dt / Mathf.Max(landingFootTime, 0.01f));
                    f.pos = Vector3.Lerp(f.landFrom, f.plant, Smooth(f.landT));
                }
                else
                {
                    f.pos = f.plant;
                }
                f.yaw = f.plantYaw;
                f.pitch = Mathf.Lerp(f.pitch, 0f, Damp(20f, dt));

                // Stretched far beyond reach (knocked away, teleported): put it back.
                float reach = ctx.Rig.Legs[i].Valid ? ctx.Rig.Legs[i].Length : 1.5f;
                Vector3 rest = RestTarget(f, ctx);
                if (HorizontalDistance(f.plant, rest) > reach * 1.6f)
                    f.plant = f.pos = OnGround(rest, ctx, rest);
            }

            f.prevP = p;
        }
    }

    bool NeedsSettle(CharacterAnimContext ctx) => FootNeedsSettle(feet[0], ctx) || FootNeedsSettle(feet[1], ctx);

    bool FootNeedsSettle(Foot f, CharacterAnimContext ctx)
    {
        if (f.swinging)
            return true;
        Vector3 rest = RestTarget(f, ctx);
        if (HorizontalDistance(f.plant, rest) > settleDistance)
            return true;
        if (Mathf.Abs(Mathf.DeltaAngle(f.plantYaw, ctx.State.FacingYaw)) > settleAngle)
            return true;
        return TryGround(rest, ctx.State.GroundHeight, out float y) && Mathf.Abs(y - f.plant.y) > 0.25f;
    }

    // Where the swinging foot will land: under the hip at mid-stance, predicted from the velocity.
    Vector3 SwingTarget(Foot f, CharacterAnimContext ctx, Vector3 velocity, float p, float cycleRate)
    {
        float timeToLand = cycleRate > 1e-4f ? (1f - p) / cycleRate : 0f;
        float stanceTime = cycleRate > 1e-4f ? duty / cycleRate : 0f;

        Vector3 rest = RestTarget(f, ctx);
        Vector3 lead = Vector3.ClampMagnitude(velocity * (stanceTime * 0.5f), maxStrideLead);
        Vector3 target = rest + velocity * timeToLand + lead;

        // Never plant inside a wall: fall back toward the hip.
        return OnGround(target, ctx, Vector3.Lerp(rest, target, 0.4f));
    }

    void UpdateSwing(Foot f, CharacterAnimContext ctx, Vector3 target, float t)
    {
        t = Mathf.Clamp01(t);
        f.target = target;
        Vector3 from = f.liftPos;
        float rise = target.y - from.y;
        bool up = rise > 0.25f;
        bool down = rise < -0.25f;

        // Stepping up: lift before moving forward. Stepping down: move out before dropping.
        float h = Smooth(Mathf.InverseLerp(up ? 0.15f : 0f, down ? 0.85f : 1f, t));

        float distance = HorizontalDistance(from, target);
        float lift = Mathf.Lerp(walkLift, runLift, runBlend) * Mathf.Clamp(distance / 0.8f, 0.35f, 1f);

        // Clear any block edge between here and the landing spot.
        float highestGround = Mathf.Max(from.y, target.y);
        for (int k = 1; k <= 3; k++)
        {
            Vector3 q = Vector3.Lerp(from, target, k / 4f);
            if (TryGround(q, highestGround, out float y))
                highestGround = Mathf.Max(highestGround, y);
        }
        float peak = Mathf.Max(Mathf.Max(from.y, target.y) + lift, highestGround + obstacleClearance + lift * 0.5f);

        float peakT = up ? 0.5f : down ? 0.3f : 0.45f;
        float y2 = t < peakT
            ? Mathf.Lerp(from.y, peak, Mathf.Sin(t / peakT * Mathf.PI * 0.5f))
            : Mathf.Lerp(peak, target.y, Smooth((t - peakT) / (1f - peakT)));

        f.pos = new Vector3(Mathf.Lerp(from.x, target.x, h), y2, Mathf.Lerp(from.z, target.z, h));
        f.yaw = Mathf.LerpAngle(f.liftYaw, ctx.State.FacingYaw, h);

        // Toes push down leaving the ground, lift to clear the edge before landing.
        float toe = Mathf.Lerp(walkToePitch, runToePitch, runBlend) * Mathf.Max(gaitWeight, 0.3f);
        f.pitch = toe * Mathf.Sin(t * 2f * Mathf.PI);
    }

    // ----------------------------------------------------------------------------------
    // Air
    // ----------------------------------------------------------------------------------

    void EnterAir(CharacterAnimContext ctx)
    {
        inAir = true;
        Quaternion inv = Quaternion.Inverse(ctx.Facing);
        foreach (var f in feet)
        {
            f.swinging = false;
            f.airLocal = inv * (f.pos - ctx.Root);
        }
    }

    void UpdateAir(CharacterAnimContext ctx)
    {
        float vy = ctx.State.Velocity.y;
        float rising = Mathf.InverseLerp(-6f, 4f, vy);
        float k = Damp(airBlendSpeed, ctx.DeltaTime);

        // The foot that was ahead stays ahead.
        int front = feet[0].airLocal.z >= feet[1].airLocal.z ? 0 : 1;
        for (int i = 0; i < 2; i++)
        {
            var f = feet[i];
            Vector3 target = new Vector3(
                f.rest.x,
                Mathf.Lerp(0.05f, airTuck, rising),
                i == front ? airStride : -airStride * 0.6f);
            f.airLocal = Vector3.Lerp(f.airLocal, target, k);
            f.pos = ctx.Root + ctx.Facing * f.airLocal;
            f.yaw = Mathf.LerpAngle(f.yaw, ctx.State.FacingYaw, k);
            f.pitch = Mathf.Lerp(f.pitch, Mathf.Lerp(18f, -8f, rising), k);
        }
    }

    void Land(CharacterAnimContext ctx, Vector3 velocity)
    {
        inAir = false;
        Vector3 lead = Vector3.ClampMagnitude(velocity * 0.08f, 0.4f);
        foreach (var f in feet)
        {
            Vector3 rest = RestTarget(f, ctx);
            f.plant = OnGround(rest + lead, ctx, rest);
            f.plantYaw = ctx.State.FacingYaw;
            f.landFrom = f.pos;
            f.landT = 0f;
            f.swinging = false;
        }
        // Left foot just touched down, right foot mid-stance.
        phase = 0.02f;
        feet[0].prevP = 0.02f;
        feet[1].prevP = 0.52f;
    }

    // ----------------------------------------------------------------------------------
    // Pelvis
    // ----------------------------------------------------------------------------------

    void UpdatePelvis(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var rig = ctx.Rig;
        // Steps: walking rides highest over the standing foot, running sinks into each stride.
        float stepPhase = Mathf.Repeat(2f * phase - duty, 1f);
        float wave = Mathf.Cos(stepPhase * 2f * Mathf.PI);
        float bob = Mathf.Lerp(walkBob * wave, -runBob * wave, runBlend) * gaitWeight;
        float sway = walkSway * Mathf.Cos((phase - duty * 0.5f) * 2f * Mathf.PI) * (1f - runBlend) * gaitWeight;
        if (inAir) bob = sway = 0f;

        float gaitCrouch = inAir ? 0f : Mathf.Lerp(walkCrouch, runCrouch, runBlend) * gaitWeight;
        Vector3 offset = new Vector3(feet[0].rest.x < 0f ? -sway : sway, bob - gaitCrouch - ctx.State.Crouch * crouchDepth, 0f);

        // Height: ride on the ground under the feet that carry the weight, so on stairs the body
        // rises as each step is taken rather than ahead of the legs.
        if (inAir || !ctx.State.Grounded)
        {
            // Off the ground: ease the offset from the body back to zero (moving with the body,
            // so a fast jump or fall doesn't leave the pelvis trailing).
            pelvisGround = ctx.Root.y + (pelvisGround - lastPelvisRootY) * Mathf.Exp(-12f * dt);
            pelvisGroundVelocity = 0f;
        }
        else
        {
            float sum = 0f, total = 0f;
            foreach (var f in feet)
            {
                float w, y;
                if (f.swinging)
                {
                    w = Smooth(Mathf.InverseLerp(0.5f, 1f, Mathf.InverseLerp(f.swingStart, 1f, f.p)));
                    y = f.target.y;
                }
                else
                {
                    w = 1f - 0.8f * gaitWeight * Smooth(Mathf.InverseLerp(duty - 0.15f, duty, f.p));
                    y = f.plant.y;
                }
                sum += w * y;
                total += w;
            }
            float feetGround = total > 0.01f ? sum / total : ctx.Root.y;
            float target = Mathf.Lerp(feetGround, ctx.Root.y, bodyHeightFollow);
            pelvisGround = Mathf.SmoothDamp(pelvisGround, target, ref pelvisGroundVelocity, groundFollowSmoothTime, Mathf.Infinity, dt);
        }
        lastPelvisRootY = ctx.Root.y;
        offset.y += pelvisGround - ctx.Root.y;

        // Lower the pelvis until the standing feet are within reach (stairs, ledges, long
        // strides). A foot about to lift is allowed to peel off instead, and a swinging foot only
        // counts as it comes down, so the body doesn't dip for the trailing leg on every step.
        float dropTarget = 0f;
        if (!inAir)
        {
            for (int i = 0; i < 2; i++)
            {
                var leg = rig.Legs[i];
                if (!leg.Valid) continue;

                var f = feet[i];
                float support = f.swinging
                    ? Smooth(Mathf.InverseLerp(0.55f, 1f, Mathf.InverseLerp(f.swingStart, 1f, f.p)))
                    : 1f - Smooth(Mathf.InverseLerp(duty - 0.12f, duty, f.p)) * gaitWeight;
                if (support <= 0f) continue;

                Vector3 hip = ctx.Root + ctx.Facing * (rig.PelvisRestPosition + offset + leg.RestUpperFromPelvis);
                Vector3 foot = (f.swinging ? f.pos : f.plant) + Vector3.up * (sole + footHeightAdjust);
                float reach = leg.Length * legReach;
                float horizontal = HorizontalDistance(hip, foot);
                float vertical = hip.y - foot.y;
                float allowed = horizontal < reach ? Mathf.Sqrt(reach * reach - horizontal * horizontal) : 0f;
                dropTarget = Mathf.Max(dropTarget, (vertical - allowed) * support);
            }
        }
        dropTarget = Mathf.Clamp(dropTarget, 0f, maxPelvisDrop);
        pelvisDrop = Mathf.SmoothDamp(pelvisDrop, dropTarget, ref pelvisDropVelocity, pelvisSmoothTime, Mathf.Infinity, dt);

        // Landing suspension.
        float omega = 2f * Mathf.PI * landingFrequency;
        compressionVelocity += (-omega * omega * compression - 2f * landingDamping * omega * compressionVelocity) * dt;
        compression = Mathf.Clamp(compression + compressionVelocity * dt, -maxLandingCompression, 0.2f);

        offset.y += compression - pelvisDrop;
        pose.PelvisOffset += offset * weight;
    }

    // ----------------------------------------------------------------------------------

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
