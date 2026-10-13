using UnityEngine;

/// <summary>
/// Dodge: a quick, low dash. The body drops and leans into the dash, the feet skim along in a
/// wide stance (the foot on the dash side leads), and the arms go out for balance. Blends in over
/// the gait while <see cref="LocomotionState.Dodging"/>; when it lets go the gait carries on from
/// where the feet ended up.
/// </summary>
public class DodgeLayer : CharacterAnimLayer
{
    [Tooltip("How far the pelvis drops during the dash")]
    [SerializeField] private float crouch = 0.3f;
    [Tooltip("Lean into the dash (degrees)")]
    [SerializeField] private float lean = 16f;
    [Tooltip("How far each foot sits ahead of / behind the body along the dash")]
    [SerializeField] private float stance = 0.5f;
    [Tooltip("How high the trailing foot lifts as it skims after the body")]
    [SerializeField] private float footLift = 0.22f;
    [SerializeField] private float armSpread = 40f;
    [SerializeField] private float elbowBend = 45f;
    [SerializeField] private float blendInSpeed = 14f;
    [SerializeField] private float blendOutSpeed = 6f;

    float amount;
    int lead;
    bool wasDodging;
    LocomotionLayer locomotion;

    public override void Initialize(CharacterAnimator animator, CharacterAnimContext context)
    {
        base.Initialize(animator, context);
        locomotion = GetComponent<LocomotionLayer>();
    }

    public override void ResetState()
    {
        amount = 0f;
        wasDodging = false;
    }

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;
        bool dodging = s.Dodging && s.Grounded;
        amount = Mathf.MoveTowards(amount, dodging ? 1f : 0f, (dodging ? blendInSpeed : blendOutSpeed) * dt);

        // Dash direction in the facing frame (the body keeps facing the same way while dodging).
        Vector3 dir = Quaternion.Inverse(ctx.Facing) * s.DodgeDirection;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.back;

        // Lead with the foot on the side of the dash; straight ahead or back, the left leads.
        if (dodging && !wasDodging)
            lead = dir.x > 0.3f ? CharacterPose.Right : CharacterPose.Left;
        wasDodging = dodging;

        if (amount <= 0f)
            return;

        float w = amount * amount * (3f - 2f * amount) * weight;
        float t = dodging ? s.DodgeProgress : 1f;
        float arc = Mathf.Sin(Mathf.PI * t);
        var rig = ctx.Rig;
        float sole = rig.SoleHeight;

        // Body: drop and lean into the dash, chest turning back a little to stay level.
        Vector3 pelvis = new Vector3(dir.x * 0.08f, -crouch * (0.6f + 0.4f * arc), dir.z * 0.08f);
        pose.PelvisOffset = Vector3.Lerp(pose.PelvisOffset, pelvis, w);
        Vector3 axis = Vector3.Cross(Vector3.up, dir);
        pose.PelvisRotation = Quaternion.Slerp(pose.PelvisRotation, Quaternion.AngleAxis(lean, axis), w);
        pose.ChestRotation = Quaternion.Slerp(pose.ChestRotation, Quaternion.AngleAxis(-lean * 0.4f, axis), w);

        for (int i = 0; i < 2; i++)
        {
            float side = i == CharacterPose.Left ? -1f : 1f;
            bool leading = i == lead;

            // Feet spread along the dash, the lead foot ahead; the trailing foot skims after it.
            Vector3 local = new Vector3(side * 0.24f, 0f, 0f) + dir * (leading ? stance : -stance);
            Vector3 p = ctx.Root + ctx.Facing * local;
            float ground = ctx.Root.y;
            if (VoxelPhysics.TryGetGroundHeight(new Vector3(p.x, s.GroundHeight, p.z), 1.5f, 2.5f, out float g))
                ground = Mathf.Max(g, ctx.Root.y - 0.8f);
            float lift = (leading ? 0.3f : 1f) * footLift * arc;

            var foot = pose.Feet[i];
            foot.Position = Vector3.Lerp(foot.Position, new Vector3(p.x, ground + sole + lift, p.z), w);
            foot.Yaw = Mathf.LerpAngle(foot.Yaw, s.FacingYaw, w);
            foot.Pitch = Mathf.Lerp(foot.Pitch, 0f, w);
            foot.Weight = Mathf.Max(foot.Weight, w);
            pose.Feet[i] = foot;
            if (locomotion != null && w > 0.3f)
                locomotion.HoldFoot(i, new Vector3(p.x, ground, p.z));

            // Arms out for balance.
            var arm = pose.Arms[i];
            Quaternion shoulder = Quaternion.AngleAxis(side * armSpread, Vector3.forward);
            arm.Shoulder = Quaternion.Slerp(arm.Shoulder, shoulder, w);
            arm.Elbow = Quaternion.Slerp(arm.Elbow, Quaternion.AngleAxis(-elbowBend, Vector3.right), w);
            pose.Arms[i] = arm;
        }
    }
}
