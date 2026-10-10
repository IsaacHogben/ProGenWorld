using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Procedural walk for the voxel character. Feet stay planted in the world and step to targets
/// predicted from the body's actual velocity; step arcs clear ledges when stepping up, the
/// pelvis drops when a foot is on lower ground, and the arms swing in time with the steps.
/// Ground is read from the voxel data (VoxelPhysics). Works with SimpleVoxelController.
/// </summary>
public class ProceduralLegIK : MonoBehaviour
{
    [Header("Leg Transforms")]
    [Tooltip("The left thigh bone (left_leg)")]
    [SerializeField] private Transform leftLeg;
    [Tooltip("The left shin bone (left_shin)")]
    [SerializeField] private Transform leftShin;
    [Tooltip("The left foot bone (left_foot)")]
    [SerializeField] private Transform leftFoot;
    [Tooltip("The right thigh bone (right_leg)")]
    [SerializeField] private Transform rightLeg;
    [Tooltip("The right shin bone (right_shin)")]
    [SerializeField] private Transform rightShin;
    [Tooltip("The right foot bone (right_foot)")]
    [SerializeField] private Transform rightFoot;

    [Header("Body Reference")]
    [Tooltip("The Body GameObject that rotates (parent of all model parts)")]
    [SerializeField] private Transform bodyTransform;
    [Tooltip("Left arm transform (optional, for arm swing)")]
    [SerializeField] private Transform leftArm;
    [Tooltip("Right arm transform (optional, for arm swing)")]
    [SerializeField] private Transform rightArm;

    [Header("General Settings")]
    [Tooltip("How far to the sides feet are placed (stance width)")]
    [SerializeField] private float footOffsetSide = 0.3f;
    [Tooltip("Height of the foot bone above the ground it stands on")]
    [SerializeField] private float footHeightOffset = 0.1f;

    [Header("Walking Settings")]
    [Tooltip("Distance foot must be from its target before triggering a step")]
    [SerializeField] private float walkStepDistance = 0.5f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float walkStepHeight = 0.3f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float walkStepDuration = 0.3f;
    [Tooltip("Furthest ahead of the hip a foot is placed when walking")]
    [SerializeField] private float walkFootOffset = 0.5f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float walkFootAngle = 25f;

    [Header("Turning / Settling Settings")]
    [Tooltip("Distance foot must be from target before triggering a step")]
    [SerializeField] private float turnStepDistance = 0.2f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float turnStepHeight = 0.2f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float turnStepDuration = 0.25f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float turnFootAngle = 15f;

    [Header("Sprinting Settings")]
    [Tooltip("Distance foot must be from target before triggering a step")]
    [SerializeField] private float sprintStepDistance = 0.7f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float sprintStepHeight = 0.4f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float sprintStepDuration = 0.2f;
    [Tooltip("Furthest ahead of the hip a foot is placed when sprinting")]
    [SerializeField] private float sprintFootOffset = 0.8f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float sprintFootAngle = 30f;

    [Header("Foot Angle Curve")]
    [Tooltip("Step progress when foot reaches maximum tilt (0-1, e.g. 0.3 = 30% through step)")]
    [SerializeField] private float tiltPeakProgress = 0.3f;
    [Tooltip("Step progress when foot starts returning to flat (0-1, e.g. 0.7 = 70% through step)")]
    [SerializeField] private float tiltReturnProgress = 0.7f;

    [Header("Airborne Pose")]
    [Tooltip("Default foot position when jumping/falling (relative to body in local space)")]
    [SerializeField] private Vector3 defaultFootOffset = new Vector3(0.3f, -0.5f, 0.2f);
    [Tooltip("Speed to lerp feet to default position when airborne")]
    [SerializeField] private float airborneTransitionSpeed = 10f;
    [Tooltip("How fast feet reach the ground when landing (higher = quicker)")]
    [SerializeField] private float landingTransitionSpeed = 15f;
    [Tooltip("Velocity threshold for full airborne pose (positive = going up)")]
    [SerializeField] private float maxUpwardVelocity = 5f;
    [Tooltip("Velocity threshold for captured pose (negative = falling)")]
    [SerializeField] private float maxDownwardVelocity = -5f;
    [Tooltip("Body dip on landing per m/s of impact speed")]
    [SerializeField] private float landingDipPerSpeed = 0.03f;
    [Tooltip("Largest landing dip")]
    [SerializeField] private float maxLandingDip = 0.4f;

    [Header("Body Lean")]
    [Tooltip("Body pitch when sprinting forward (in degrees)")]
    [SerializeField] private float sprintTiltAngle = 10f;
    [Tooltip("Body pitch when walking forward (in degrees, same sign as sprint)")]
    [SerializeField] private float walkTiltAngle = 3f;
    [Tooltip("How fast body tilts into/out of a lean")]
    [SerializeField] private float sprintTiltSpeed = 5f;
    [Tooltip("Vertical bob of the body over each step")]
    [SerializeField] private float walkBobAmount = 0.06f;

    [Header("Arm Swing")]
    [Tooltip("Enable arm swing animation")]
    [SerializeField] private bool enableArmSwing = true;
    [Tooltip("Maximum arm swing angle forward/back")]
    [SerializeField] private float armSwingAngle = 30f;
    [Tooltip("How quickly arms follow the gait")]
    [SerializeField] private float armSwingSpeed = 10f;
    [Tooltip("Flip if arms swing with the leg on the same side instead of against it")]
    [SerializeField] private bool invertArmSwing = false;

    [Header("Ground Detection")]
    [Tooltip("Height above the feet where ground probes start")]
    [SerializeField] private float raycastStartHeight = 2f;
    [Tooltip("Highest ledge a foot will be placed on")]
    [SerializeField] private float maxFootStepUp = 1.25f;
    [Tooltip("How far below the body feet may reach (and the body may lower) for step-down (1 = one block)")]
    [SerializeField] private float stepDownDistance = 1f;
    [Tooltip("Speed at which body adjusts height")]
    [SerializeField] private float bodyHeightAdjustSpeed = 5f;

    class Leg
    {
        public Transform thigh, shin, foot;
        public bool isLeft;
        public float side;          // +1 along the body's right axis (the model's left)

        public Vector3 pos;         // animated foot position (world)
        public Vector3 planted;     // where the foot rests when not stepping
        public float angle;         // foot pitch

        public bool stepping;
        public Vector3 from, to;
        public float t, duration, lift, maxAngle;

        public Vector3 airLocal;    // foot position in body space when leaving the ground

        public bool Valid => thigh != null && shin != null && foot != null;
        public Vector3 Support => stepping ? to : planted;
    }

    struct GaitParams
    {
        public float stepDistance, stepHeight, duration, footOffset, footAngle;
    }

    private SimpleVoxelController controller;
    private VoxelBody body;

    private Leg left, right;

    private float bodyHeightOffset;   // rest height of the body above the root
    private float currentBodyHeight;  // step-down lowering
    private float currentBob;
    private float landingDip;
    private float currentBodyTilt;
    private float currentArmSwing;
    private float gaitPhase;

    private Vector3 lastRootPos;
    private Vector3 velocity;         // smoothed horizontal velocity of the rendered body
    private bool wasAirborne;
    private float minAirVelocity;

    void Start()
    {
        controller = GetComponent<SimpleVoxelController>();
        body = GetComponent<VoxelBody>();

        if (bodyTransform != null)
            bodyHeightOffset = bodyTransform.localPosition.y;

        left = new Leg { thigh = leftLeg, shin = leftShin, foot = leftFoot, isLeft = true, side = 1f };
        right = new Leg { thigh = rightLeg, shin = rightShin, foot = rightFoot, isLeft = false, side = -1f };
        foreach (var leg in new[] { left, right })
        {
            if (leg.foot != null)
                leg.pos = leg.planted = leg.foot.position;
        }

        lastRootPos = transform.position;
    }

    void LateUpdate()
    {
        if (bodyTransform == null)
            return;

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        if (UpdateVelocity(dt))
            ResetFeet();

        bool grounded = controller == null || controller.IsGrounded;
        if (grounded)
        {
            if (wasAirborne)
                Land();
            UpdateGait(dt);
        }
        else
        {
            if (!wasAirborne)
                BeginAirborne();
            UpdateAirborne(dt);
        }
        wasAirborne = !grounded;

        UpdateBody(dt, grounded);
        UpdateArmSwing(dt, grounded);
        ApplyIK();
    }

    // ----------------------------------------------------------------------------------
    // Motion sampling
    // ----------------------------------------------------------------------------------

    // Returns true when the body teleported.
    bool UpdateVelocity(float dt)
    {
        Vector3 p = transform.position;
        Vector3 raw = (p - lastRootPos) / dt;
        lastRootPos = p;
        raw.y = 0f;

        if (raw.sqrMagnitude > 50f * 50f)
        {
            velocity = Vector3.zero;
            return true;
        }

        velocity = Vector3.Lerp(velocity, raw, 1f - Mathf.Exp(-12f * dt));
        return false;
    }

    // Height of the simulated body (the rendered one eases behind it after a step).
    float SimRootY => transform.position.y - (body != null ? body.VisualOffset : 0f);

    Vector3 FlatRight
    {
        get
        {
            Vector3 r = bodyTransform.right;
            r.y = 0f;
            return r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
        }
    }

    // The model faces away from the body transform's forward axis.
    Vector3 ModelForward
    {
        get
        {
            Vector3 f = -bodyTransform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }
    }

    GaitParams CurrentParams()
    {
        var state = controller != null ? controller.State : SimpleVoxelController.MovementState.Walking;
        switch (state)
        {
            case SimpleVoxelController.MovementState.Idle:
            case SimpleVoxelController.MovementState.Turning:
                return new GaitParams { stepDistance = turnStepDistance, stepHeight = turnStepHeight, duration = turnStepDuration, footOffset = 0f, footAngle = turnFootAngle };
            case SimpleVoxelController.MovementState.Sprinting:
                return new GaitParams { stepDistance = sprintStepDistance, stepHeight = sprintStepHeight, duration = sprintStepDuration, footOffset = sprintFootOffset, footAngle = sprintFootAngle };
            default:
                return new GaitParams { stepDistance = walkStepDistance, stepHeight = walkStepHeight, duration = walkStepDuration, footOffset = walkFootOffset, footAngle = walkFootAngle };
        }
    }

    // ----------------------------------------------------------------------------------
    // Ground probing
    // ----------------------------------------------------------------------------------

    /// <summary>Ground height under a point, or false if it is a wall, a drop, or not loaded.</summary>
    bool TryGroundAt(Vector3 point, float refY, out float groundY)
    {
        groundY = refY;
        Vector3 start = new Vector3(point.x, refY + raycastStartHeight, point.z);

        // Probe starting inside a block means a wall taller than we can step onto.
        if (VoxelPhysics.IsSolid((int3)math.floor((float3)start)))
            return false;

        if (!VoxelPhysics.Raycast(start, Vector3.down, out VoxelRaycastHit hit, raycastStartHeight + stepDownDistance + 0.25f))
            return false;

        if (hit.point.y - refY > maxFootStepUp)
            return false;

        groundY = hit.point.y;
        return true;
    }

    Vector3 Anchor(Leg leg) => transform.position + FlatRight * (leg.side * footOffsetSide);

    // Where the foot should land: under the hip, led along the velocity by half a step.
    Vector3 FootTarget(Leg leg, in GaitParams p)
    {
        float refY = SimRootY;
        Vector3 anchor = Anchor(leg);
        Vector3 lead = Vector3.ClampMagnitude(velocity * (p.duration * 0.5f), p.footOffset);

        if (TryGroundAt(anchor + lead, refY, out float y))
            return new Vector3(anchor.x + lead.x, y + footHeightOffset, anchor.z + lead.z);
        if (TryGroundAt(anchor, refY, out y))
            return new Vector3(anchor.x, y + footHeightOffset, anchor.z);
        return new Vector3(anchor.x, refY + footHeightOffset, anchor.z);
    }

    void ResetFeet()
    {
        var p = CurrentParams();
        foreach (var leg in new[] { left, right })
        {
            leg.stepping = false;
            leg.pos = leg.planted = FootTarget(leg, p);
            leg.angle = 0f;
        }
    }

    // ----------------------------------------------------------------------------------
    // Gait
    // ----------------------------------------------------------------------------------

    void UpdateGait(float dt)
    {
        var p = CurrentParams();
        Vector3 leftTarget = FootTarget(left, p);
        Vector3 rightTarget = FootTarget(right, p);

        AdvanceStep(left, leftTarget, dt);
        AdvanceStep(right, rightTarget, dt);

        // One foot lifts at a time, except a nearly finished step can overlap the next one.
        bool leftFree = !left.stepping && (!right.stepping || right.t > 0.85f);
        bool rightFree = !right.stepping && (!left.stepping || left.t > 0.85f);

        float leftError = Vector3.Distance(left.planted, leftTarget);
        float rightError = Vector3.Distance(right.planted, rightTarget);

        // Too far from the hip to keep standing on: step regardless of the other foot.
        float maxReach = Mathf.Max(p.stepDistance, walkStepDistance) * 3f;
        if (!left.stepping && HorizontalDistance(left.planted, Anchor(left)) > maxReach) leftFree = true;
        if (!right.stepping && HorizontalDistance(right.planted, Anchor(right)) > maxReach) rightFree = true;

        Leg candidate = null;
        Vector3 candidateTarget = default;
        if (leftFree && leftError > p.stepDistance && (!rightFree || leftError >= rightError))
        {
            candidate = left;
            candidateTarget = leftTarget;
        }
        else if (rightFree && rightError > p.stepDistance)
        {
            candidate = right;
            candidateTarget = rightTarget;
        }

        if (candidate != null)
        {
            float error = candidate == left ? leftError : rightError;
            float lift = p.stepHeight * Mathf.Clamp(error / Mathf.Max(p.stepDistance * 2f, 0.01f), 0.3f, 1f);
            StartStep(candidate, candidateTarget, p.duration, lift, p.footAngle);
        }

        if (!left.stepping) left.pos = left.planted;
        if (!right.stepping) right.pos = right.planted;
    }

    void StartStep(Leg leg, Vector3 target, float duration, float lift, float maxAngle)
    {
        leg.stepping = true;
        leg.from = leg.pos;
        leg.to = target;
        leg.t = 0f;
        leg.duration = Mathf.Max(duration, 0.05f);
        leg.lift = lift;
        leg.maxAngle = maxAngle;
    }

    void AdvanceStep(Leg leg, Vector3 latestTarget, float dt)
    {
        if (!leg.stepping)
            return;

        leg.t += dt / leg.duration;

        // Track the moving target during the first part of the swing so speed changes don't overshoot.
        if (leg.t < 0.6f)
            leg.to = latestTarget;

        if (leg.t >= 1f)
        {
            leg.stepping = false;
            leg.t = 0f;
            leg.pos = leg.planted = leg.to;
            leg.angle = 0f;
            return;
        }

        leg.pos = StepCurve(leg.from, leg.to, leg.t, leg.lift);
        leg.angle = CalculateFootAngle(leg.t, leg.maxAngle);
    }

    // Ease in/out across, rise early when stepping up and drop late when stepping down, so the
    // foot clears block edges.
    static Vector3 StepCurve(Vector3 from, Vector3 to, float t, float lift)
    {
        float h = t * t * (3f - 2f * t);
        Vector3 p = Vector3.Lerp(from, to, h);

        float dy = to.y - from.y;
        float v = dy > 0f ? 1f - (1f - t) * (1f - t) : t * t;
        float arc = Mathf.Sin(t * Mathf.PI) * (lift + Mathf.Max(0f, dy) * 0.3f);
        p.y = from.y + dy * v + arc;
        return p;
    }

    float CalculateFootAngle(float progress, float maxAngle)
    {
        if (progress < tiltPeakProgress)
            return Mathf.Lerp(0f, maxAngle, progress / tiltPeakProgress);
        if (progress < tiltReturnProgress)
            return maxAngle;
        return Mathf.Lerp(maxAngle, 0f, (progress - tiltReturnProgress) / (1f - tiltReturnProgress));
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = b.y = 0f;
        return Vector3.Distance(a, b);
    }

    // ----------------------------------------------------------------------------------
    // Airborne
    // ----------------------------------------------------------------------------------

    void BeginAirborne()
    {
        foreach (var leg in new[] { left, right })
        {
            leg.airLocal = bodyTransform.InverseTransformPoint(leg.pos);
            leg.stepping = false;
        }
        minAirVelocity = 0f;
    }

    void UpdateAirborne(float dt)
    {
        float verticalVelocity = body != null ? body.linearVelocity.y : 0f;
        minAirVelocity = Mathf.Min(minAirVelocity, verticalVelocity);

        // Going up: tucked pose. Falling fast: back toward the pose held at take-off.
        float blend = Mathf.InverseLerp(maxDownwardVelocity, maxUpwardVelocity, verticalVelocity);
        float k = 1f - Mathf.Exp(-airborneTransitionSpeed * dt);

        foreach (var leg in new[] { left, right })
        {
            float x = leg.isLeft ? -defaultFootOffset.x : defaultFootOffset.x;
            Vector3 airPose = new Vector3(x, defaultFootOffset.y, defaultFootOffset.z);
            Vector3 target = bodyTransform.TransformPoint(Vector3.Lerp(leg.airLocal, airPose, blend));

            leg.pos = Vector3.Lerp(leg.pos, target, k);
            leg.angle = Mathf.Lerp(leg.angle, 0f, k);
        }
    }

    void Land()
    {
        landingDip = Mathf.Min(maxLandingDip, landingDipPerSpeed * -minAirVelocity);

        // Both feet reach for the ground together.
        var p = CurrentParams();
        float duration = Mathf.Clamp(1.5f / Mathf.Max(landingTransitionSpeed, 0.01f), 0.05f, 0.2f);
        StartStep(left, FootTarget(left, p), duration, 0f, 0f);
        StartStep(right, FootTarget(right, p), duration, 0f, 0f);
    }

    // ----------------------------------------------------------------------------------
    // Body and arms
    // ----------------------------------------------------------------------------------

    void UpdateBody(float dt, bool grounded)
    {
        float speed = velocity.magnitude;
        float walkSpeed = controller != null ? controller.WalkSpeed : 5f;
        float speedFactor = Mathf.Clamp01(speed / Mathf.Max(walkSpeed, 0.01f));

        // Lower the body so the lowest foot can still reach its ground (stairs, ledges).
        float targetHeight = 0f;
        if (grounded)
        {
            float lowest = Mathf.Min(left.Support.y, right.Support.y) - footHeightOffset;
            targetHeight = Mathf.Clamp(lowest - transform.position.y, -stepDownDistance, 0f);
        }
        currentBodyHeight = Mathf.Lerp(currentBodyHeight, targetHeight, 1f - Mathf.Exp(-bodyHeightAdjustSpeed * dt));

        // Lowest at foot contact, highest mid-swing.
        float targetBob = 0f;
        if (grounded && speedFactor > 0.01f)
        {
            Leg active = left.stepping ? left : right.stepping ? right : null;
            float swing = active != null ? Mathf.Sin(active.t * Mathf.PI) : 0f;
            targetBob = walkBobAmount * speedFactor * (swing - 1f);
        }
        currentBob = Mathf.Lerp(currentBob, targetBob, 1f - Mathf.Exp(-20f * dt));

        landingDip = Mathf.Lerp(landingDip, 0f, 1f - Mathf.Exp(-6f * dt));

        float crouchOffset = controller != null ? controller.CrouchOffset : 0f;
        Vector3 bodyPos = bodyTransform.localPosition;
        bodyPos.y = bodyHeightOffset + currentBodyHeight + currentBob - landingDip + crouchOffset;
        bodyTransform.localPosition = bodyPos;

        // Lean into forward motion; more when sprinting.
        float targetTilt = 0f;
        if (grounded && speed > 0.1f)
        {
            bool sprinting = controller != null && controller.State == SimpleVoxelController.MovementState.Sprinting;
            float forwardFraction = Vector3.Dot(velocity / speed, ModelForward);
            targetTilt = (sprinting ? sprintTiltAngle : walkTiltAngle) * forwardFraction * speedFactor;
        }
        currentBodyTilt = Mathf.Lerp(currentBodyTilt, targetTilt, 1f - Mathf.Exp(-sprintTiltSpeed * dt));

        // Yaw is owned by the controller; only add the lean.
        float yaw = controller != null ? controller.BodyYaw : bodyTransform.eulerAngles.y;
        bodyTransform.rotation = Quaternion.Euler(currentBodyTilt, yaw, 0f);
    }

    void UpdateArmSwing(float dt, bool grounded)
    {
        if (!enableArmSwing || leftArm == null || rightArm == null)
            return;

        // Gait phase: left step covers 0..0.5, right step 0.5..1.
        if (left.stepping) gaitPhase = 0.5f * left.t;
        else if (right.stepping) gaitPhase = 0.5f + 0.5f * right.t;

        float target = 0f;
        if (grounded)
        {
            float walkSpeed = controller != null ? controller.WalkSpeed : 5f;
            float speedFactor = Mathf.Clamp01(velocity.magnitude / Mathf.Max(walkSpeed, 0.01f));
            target = Mathf.Cos(gaitPhase * 2f * Mathf.PI) * armSwingAngle * speedFactor;
            if (invertArmSwing) target = -target;
        }
        currentArmSwing = Mathf.Lerp(currentArmSwing, target, 1f - Mathf.Exp(-armSwingSpeed * dt));

        leftArm.localRotation = Quaternion.Euler(currentArmSwing, 0, 0);
        rightArm.localRotation = Quaternion.Euler(-currentArmSwing, 0, 0);
    }

    // ----------------------------------------------------------------------------------
    // IK
    // ----------------------------------------------------------------------------------

    void ApplyIK()
    {
        if (left.Valid) SolveLegIK(left.thigh, left.shin, left.foot, left.pos, left.angle, true);
        if (right.Valid) SolveLegIK(right.thigh, right.shin, right.foot, right.pos, right.angle, false);
    }

    void SolveLegIK(Transform thigh, Transform shin, Transform foot, Vector3 targetPos, float footAngle, bool isLeftLeg)
    {
        Vector3 thighPos = thigh.position;
        Vector3 shinPos = shin.position;

        float thighLength = Vector3.Distance(thighPos, shinPos);
        float shinLength = Vector3.Distance(shinPos, foot.position);
        float totalLength = thighLength + shinLength;

        Vector3 toTarget = targetPos - thighPos;
        float targetDistance = Mathf.Min(toTarget.magnitude, totalLength * 0.99f);
        Vector3 targetDirection = toTarget.normalized;

        // Law of cosines
        float thighAngle = Mathf.Acos(Mathf.Clamp(
            (thighLength * thighLength + targetDistance * targetDistance - shinLength * shinLength) /
            (2f * thighLength * targetDistance), -1f, 1f));

        // Pole direction (which way the knee points)
        Vector3 poleDirection = bodyTransform.forward;
        Vector3 bendAxis = Vector3.Cross(targetDirection, poleDirection).normalized;
        if (bendAxis.magnitude < 0.01f)
            bendAxis = bodyTransform.right * (isLeftLeg ? -1 : 1);

        Vector3 kneeDirection = Quaternion.AngleAxis(-thighAngle * Mathf.Rad2Deg, bendAxis) * targetDirection;
        Vector3 kneePos = thighPos + kneeDirection * thighLength;

        Vector3 thighDir = (kneePos - thighPos).normalized;
        thigh.rotation = Quaternion.LookRotation(bendAxis, -thighDir) * Quaternion.Euler(0, 90, 0);

        Vector3 shinDir = (targetPos - kneePos).normalized;
        shin.rotation = Quaternion.LookRotation(bendAxis, -shinDir) * Quaternion.Euler(0, 90, 0);

        // Foot level with the ground, facing the body direction, pitched by the step tilt.
        Quaternion baseRotation = Quaternion.Euler(0, bodyTransform.eulerAngles.y, 0);
        Quaternion tiltRotation = Quaternion.AngleAxis(-footAngle, bodyTransform.right);
        foot.rotation = tiltRotation * baseRotation;
    }
}
