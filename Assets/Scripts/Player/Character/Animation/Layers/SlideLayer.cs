using UnityEngine;

/// <summary>
/// Slide pose: low to the ground leaning back, front leg out with the toes up, back leg tucked,
/// arms out for balance. Blends in over the gait while <see cref="LocomotionState.Sliding"/> and
/// back out when the slide ends. The feet follow the ground under them.
/// </summary>
public class SlideLayer : CharacterAnimLayer
{
    [Tooltip("Pelvis height above the ground while sliding")]
    [SerializeField] private float pelvisHeight = 0.8f;
    [Tooltip("Lean back (degrees)")]
    [SerializeField] private float leanBack = 22f;
    [Tooltip("Chest curls forward to look where it's going (degrees)")]
    [SerializeField] private float chestForward = 14f;
    [Tooltip("How far ahead of the body the front foot reaches")]
    [SerializeField] private float frontFootReach = 1.05f;
    [Tooltip("Where the tucked foot sits (behind the hip)")]
    [SerializeField] private float backFootOffset = -0.5f;
    [Tooltip("Height of the tucked foot above the ground, so the shin rides just over it")]
    [SerializeField] private float backFootLift = 0.15f;
    [Tooltip("Which leg leads: 1 = right, 0 = left")]
    [SerializeField] private int leadLeg = 1;
    [SerializeField] private float blendInSpeed = 9f;
    [SerializeField] private float blendOutSpeed = 5f;

    float amount;

    public override void ResetState() => amount = 0f;

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        bool sliding = ctx.State.Sliding;
        amount = Mathf.MoveTowards(amount, sliding ? 1f : 0f, (sliding ? blendInSpeed : blendOutSpeed) * dt);
        if (amount <= 0f)
            return;

        float w = amount * amount * (3f - 2f * amount) * weight;
        var rig = ctx.Rig;
        float sole = rig.SoleHeight;

        // Body low and leaning back.
        Vector3 pelvis = new Vector3(0f, pelvisHeight - rig.PelvisRestPosition.y, -0.15f);
        pose.PelvisOffset = Vector3.Lerp(pose.PelvisOffset, pelvis, w);
        pose.PelvisRotation = Quaternion.Slerp(pose.PelvisRotation, Quaternion.Euler(-leanBack, 0f, 0f), w);
        pose.ChestRotation = Quaternion.Slerp(pose.ChestRotation, Quaternion.Euler(chestForward, 0f, 0f), w);

        for (int i = 0; i < 2; i++)
        {
            float side = i == CharacterPose.Left ? -1f : 1f;
            bool lead = i == leadLeg;
            Vector3 local = lead
                ? new Vector3(side * 0.18f, 0f, frontFootReach)
                : new Vector3(side * 0.32f, 0f, backFootOffset);
            Vector3 p = ctx.Root + ctx.Facing * local;
            // On the ground the feet follow it; in the air (off a step at speed) they hold the pose.
            float ground = ctx.Root.y;
            if (ctx.State.Grounded && VoxelPhysics.TryGetGroundHeight(new Vector3(p.x, ctx.State.GroundHeight, p.z), 1.5f, 2.5f, out float g))
                ground = Mathf.Max(g, ctx.Root.y - 0.6f);

            var foot = pose.Feet[i];
            Vector3 target = new Vector3(p.x, ground + sole + (lead ? 0f : backFootLift), p.z);
            foot.Position = Vector3.Lerp(foot.Position, target, w);
            foot.Yaw = Mathf.LerpAngle(foot.Yaw, ctx.State.FacingYaw, w);
            foot.Pitch = Mathf.Lerp(foot.Pitch, lead ? -25f : 60f, w);
            foot.Weight = Mathf.Max(foot.Weight, w);
            pose.Feet[i] = foot;

            // Arm on the tucked side reaches back toward the ground, the other is out front.
            var arm = pose.Arms[i];
            Quaternion shoulder = lead
                ? Quaternion.AngleAxis(side * 25f, Vector3.forward) * Quaternion.AngleAxis(-55f, Vector3.right)
                : Quaternion.AngleAxis(side * 35f, Vector3.forward) * Quaternion.AngleAxis(35f, Vector3.right);
            Quaternion elbow = Quaternion.AngleAxis(lead ? -35f : -10f, Vector3.right);
            arm.Shoulder = Quaternion.Slerp(arm.Shoulder, shoulder, w);
            arm.Elbow = Quaternion.Slerp(arm.Elbow, elbow, w);
            pose.Arms[i] = arm;
        }
    }
}
