using UnityEngine;

/// <summary>
/// Poses in the air on top of the gait's simple tuck:
/// - Sprint jump: a running leap - lead leg reaching out in front, back leg trailing with the
///   heel kicked up, arms opposite the legs, body leaning into it. The lead foot drops to meet
///   the ground on the way down.
/// - Falling: on a long drop the legs hang apart, the arms go up and out and flail.
/// </summary>
public class AirPoseLayer : CharacterAnimLayer
{
    [Header("Sprint Jump")]
    [Tooltip("Take-off speed (as a fraction of the sprint speed) where the leap starts, and where it is full")]
    [SerializeField] private float leapStartSpeed = 0.5f;
    [SerializeField] private float leapFullSpeed = 0.85f;
    [Tooltip("Lead foot ahead of the body, and its height rising / about to land")]
    [SerializeField] private float leadReach = 0.75f;
    [SerializeField] private float leadLift = 0.55f;
    [SerializeField] private float leadLandingLift = 0.1f;
    [Tooltip("Trailing foot behind the body and kicked up")]
    [SerializeField] private float trailReach = 0.7f;
    [SerializeField] private float trailLift = 0.85f;
    [Tooltip("Arm swing forward / back (degrees)")]
    [SerializeField] private float leapArmForward = 75f;
    [SerializeField] private float leapArmBack = 45f;
    [SerializeField] private float leapLean = 10f;

    [Header("Falling")]
    [Tooltip("Downward speed (m/s) where the fall pose starts, and where it is full")]
    [SerializeField] private float fallStartSpeed = 5f;
    [SerializeField] private float fallFullSpeed = 12f;
    [Tooltip("Arms up and out (degrees from hanging)")]
    [SerializeField] private float fallArmSpread = 105f;
    [SerializeField] private float flailAngle = 22f;
    [Tooltip("Flails per second")]
    [SerializeField] private float flailRate = 1.6f;
    [SerializeField] private float fallFootSpread = 0.4f;
    [SerializeField] private float fallFootLift = 0.35f;

    [SerializeField] private float blendSpeed = 6f;

    bool wasGrounded = true;
    float leap;
    int lead;
    float fall;
    float flail;

    public override void ResetState()
    {
        wasGrounded = true;
        leap = fall = 0f;
    }

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;

        // Take-off: how fast, and which foot was in front.
        if (wasGrounded && !s.Grounded)
        {
            float speed = new Vector2(s.Velocity.x, s.Velocity.z).magnitude;
            float sprint = Mathf.Max(s.SprintSpeed, 0.1f);
            leap = Smooth(Mathf.InverseLerp(leapStartSpeed * sprint, leapFullSpeed * sprint, speed));
            float l = Vector3.Dot(pose.Feet[CharacterPose.Left].Position - ctx.Root, ctx.Forward);
            float r = Vector3.Dot(pose.Feet[CharacterPose.Right].Position - ctx.Root, ctx.Forward);
            lead = l >= r ? CharacterPose.Left : CharacterPose.Right;
        }
        wasGrounded = s.Grounded;

        float targetFall = s.Grounded || s.Swimming ? 0f : Smooth(Mathf.InverseLerp(fallStartSpeed, fallFullSpeed, -s.Velocity.y));
        fall = Mathf.MoveTowards(fall, Mathf.Max(targetFall, s.Grounded ? 0f : fall * 0.98f), blendSpeed * 0.5f * dt);
        flail += flailRate * dt;

        float air = s.Swimming ? 0f : pose.Airborne;
        float leapW = air * leap * (1f - fall) * weight;
        float fallW = air * fall * weight;
        if (leapW <= 0.001f && fallW <= 0.001f)
            return;

        var rig = ctx.Rig;
        float sole = rig.SoleHeight;
        float descend = Smooth(Mathf.InverseLerp(1f, -5f, s.Velocity.y));

        // Body: lean into the leap.
        Quaternion lean = Quaternion.Euler(leapLean * leapW, 0f, 0f);
        pose.PelvisRotation = Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(leapLean * 0.4f, 0f, 0f), leapW) * pose.PelvisRotation;
        pose.ChestRotation = lean * pose.ChestRotation;

        for (int i = 0; i < 2; i++)
        {
            float side = i == CharacterPose.Left ? -1f : 1f;
            bool leading = i == lead;
            float wave = Mathf.Sin(2f * Mathf.PI * flail + i * Mathf.PI);

            // Legs.
            Vector3 leapFoot = leading
                ? new Vector3(side * 0.2f, Mathf.Lerp(leadLift, leadLandingLift, descend), leadReach)
                : new Vector3(side * 0.22f, trailLift * Mathf.Lerp(1f, 0.7f, descend), -trailReach);
            Vector3 fallFoot = new Vector3(side * fallFootSpread, fallFootLift + 0.12f * wave, 0.12f * Mathf.Cos(2f * Mathf.PI * flail + i * Mathf.PI));
            var foot = pose.Feet[i];
            foot.Position = Vector3.Lerp(foot.Position, ctx.Root + ctx.Facing * leapFoot + Vector3.up * sole, leapW);
            foot.Position = Vector3.Lerp(foot.Position, ctx.Root + ctx.Facing * fallFoot + Vector3.up * sole, fallW);
            foot.Pitch = Mathf.Lerp(foot.Pitch, leading ? -10f : 55f, leapW);
            foot.Pitch = Mathf.Lerp(foot.Pitch, 25f, fallW);
            foot.Yaw = Mathf.LerpAngle(foot.Yaw, s.FacingYaw, Mathf.Max(leapW, fallW));
            foot.Weight = Mathf.Max(foot.Weight, Mathf.Max(leapW, fallW));
            pose.Feet[i] = foot;

            // Arms: opposite the legs in the leap; up, out and flailing in a fall.
            var arm = pose.Arms[i];
            Quaternion leapShoulder = Quaternion.AngleAxis(side * 12f, Vector3.forward) *
                                      Quaternion.AngleAxis(leading ? leapArmBack : -leapArmForward, Vector3.right);
            Quaternion fallShoulder = Quaternion.AngleAxis(side * (fallArmSpread + flailAngle * wave), Vector3.forward) *
                                      Quaternion.AngleAxis(-flailAngle * 0.6f * Mathf.Cos(2f * Mathf.PI * flail + i * Mathf.PI), Vector3.right);
            arm.Shoulder = Quaternion.Slerp(arm.Shoulder, leapShoulder, leapW);
            arm.Shoulder = Quaternion.Slerp(arm.Shoulder, fallShoulder, fallW);
            arm.Elbow = Quaternion.Slerp(arm.Elbow, Quaternion.AngleAxis(leading ? -30f : -55f, Vector3.right), leapW);
            arm.Elbow = Quaternion.Slerp(arm.Elbow, Quaternion.AngleAxis(-25f, Vector3.right), fallW);
            pose.Arms[i] = arm;
        }
    }

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
