using UnityEngine;

/// <summary>
/// Crawling flat on the belly (low enough for one block high gaps): body prone, head up looking
/// ahead, arms reaching forward and pulling, legs pushing with the knees out to the side.
/// Blends in over everything else while <see cref="LocomotionState.Crawling"/>.
/// </summary>
public class CrawlLayer : CharacterAnimLayer
{
    [Tooltip("Pelvis height above the ground lying down")]
    [SerializeField] private float pelvisHeight = 0.38f;
    [Tooltip("Pelvis behind the body's centre, so the body lies centred on it")]
    [SerializeField] private float pelvisBack = 0.45f;
    [Tooltip("How far a hand or knee moves per stroke")]
    [SerializeField] private float stroke = 0.55f;
    [Tooltip("Part of each stroke spent pulling (the rest reaches forward again)")]
    [Range(0.3f, 0.9f)] [SerializeField] private float pullFraction = 0.65f;
    [Tooltip("Head tilted back to look ahead (degrees)")]
    [SerializeField] private float headUp = 70f;
    [Tooltip("Smoothing of the body's height (climbing a block while crawling is a jump in height)")]
    [SerializeField] private float heightSmoothTime = 0.18f;
    [SerializeField] private float blendSpeed = 2.5f;

    float amount;
    float phase;
    float smoothY, smoothYVelocity;

    public override void ResetState()
    {
        amount = 0f;
        smoothYVelocity = 0f;
    }

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;
        amount = Mathf.MoveTowards(amount, s.Crawling ? 1f : 0f, blendSpeed * dt);
        if (amount <= 0f)
        {
            smoothY = ctx.Root.y;
            return;
        }

        float w = amount * amount * (3f - 2f * amount) * weight;
        smoothY = Mathf.SmoothDamp(smoothY, ctx.Root.y, ref smoothYVelocity, heightSmoothTime, Mathf.Infinity, dt);
        float yOffset = smoothY - ctx.Root.y;
        var rig = ctx.Rig;
        float sole = rig.SoleHeight;

        // Strokes keep pace with the body: during the pull a planted hand or knee moves back as
        // fast as the body moves forward.
        float speed = new Vector2(s.Velocity.x, s.Velocity.z).magnitude;
        phase = Mathf.Repeat(phase + speed * pullFraction / Mathf.Max(stroke, 0.05f) * dt, 1f);

        // Body flat, rocking a little with the strokes.
        float rock = Mathf.Sin(phase * 2f * Mathf.PI) * Mathf.Clamp01(speed);
        Vector3 pelvis = new Vector3(0f, pelvisHeight - rig.PelvisRestPosition.y + yOffset, -pelvisBack);
        pose.PelvisOffset = Vector3.Lerp(pose.PelvisOffset, pelvis, w);
        pose.PelvisRotation = Quaternion.Slerp(pose.PelvisRotation, Quaternion.Euler(88f, 0f, 5f * rock), w);
        pose.ChestRotation = Quaternion.Slerp(pose.ChestRotation, Quaternion.Euler(-8f, 6f * rock, 0f), w);
        pose.HeadRotation = Quaternion.Slerp(pose.HeadRotation, Quaternion.Euler(-headUp, 0f, 0f), w);

        float hipZ = -pelvisBack - 0.13f;
        for (int i = 0; i < 2; i++)
        {
            float side = i == CharacterPose.Left ? -1f : 1f;
            // Left arm with right leg, and the other way round.
            float armReach = Reach(Mathf.Repeat(phase + 0.5f * i, 1f), out float armLift);
            float legDraw = Reach(Mathf.Repeat(phase + 0.5f * (1 - i), 1f), out float legLift);

            // Legs out behind, knee drawn up to the side to push.
            Vector3 footLocal = new Vector3(side * (0.28f + 0.3f * legDraw), 0.12f + 0.08f * legLift + yOffset, hipZ - 1.35f + stroke * legDraw);
            var foot = pose.Feet[i];
            foot.Position = Vector3.Lerp(foot.Position, ctx.Root + ctx.Facing * footLocal + Vector3.up * (sole * 0.4f), w);
            foot.Pitch = Mathf.Lerp(foot.Pitch, 75f, w);
            foot.Yaw = Mathf.LerpAngle(foot.Yaw, s.FacingYaw, w);
            foot.Weight = Mathf.Max(foot.Weight, w);
            foot.Knee = Vector3.Lerp(foot.Knee, new Vector3(side * 0.9f, 0.25f, 0f), w);
            pose.Feet[i] = foot;

            // Hands planted ahead, pulling the body along.
            Vector3 handLocal = new Vector3(side * 0.42f, 0.1f + 0.15f * armLift + yOffset, 0.3f + stroke * armReach);
            var arm = pose.Arms[i];
            arm.HandTarget = ctx.Root + ctx.Facing * handLocal;
            arm.HandWeight = Mathf.Max(arm.HandWeight, w);
            pose.Arms[i] = arm;
        }
    }

    // 1 = reached forward (just planted) .. 0 = pulled back. Pulls straight back, then lifts and
    // swings forward again.
    float Reach(float t, out float lift)
    {
        if (t < pullFraction)
        {
            lift = 0f;
            return 1f - t / pullFraction;
        }
        float u = (t - pullFraction) / (1f - pullFraction);
        lift = Mathf.Sin(u * Mathf.PI);
        return u * u * (3f - 2f * u);
    }
}
