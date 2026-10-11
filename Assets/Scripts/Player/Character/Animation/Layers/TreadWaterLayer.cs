using UnityEngine;

/// <summary>
/// Treading water: legs cycle under the body, arms scull out to the sides, the body bobs gently.
/// Swimming forward, the body leans into it, the legs kick out behind and the arms pull forward.
/// Blends in over the other layers while <see cref="LocomotionState.Swimming"/>.
/// </summary>
public class TreadWaterLayer : CharacterAnimLayer
{
    [Header("Timing")]
    [Tooltip("Leg/arm cycles per second treading in place")]
    [SerializeField] private float treadRate = 0.9f;
    [Tooltip("Cycles per second swimming at full speed")]
    [SerializeField] private float swimRate = 1.4f;
    [Tooltip("Speed treated as full swimming speed for blending")]
    [SerializeField] private float swimSpeedReference = 3f;

    [Header("Legs")]
    [Tooltip("How high the feet are drawn up under the body (knees bent)")]
    [SerializeField] private float footLift = 0.45f;
    [Tooltip("Size of the circle each foot traces")]
    [SerializeField] private float footCircle = 0.16f;
    [Tooltip("Swimming forward, the feet trail this far behind")]
    [SerializeField] private float swimKickBack = 0.45f;

    [Header("Arms (degrees)")]
    [SerializeField] private float armSpread = 50f;
    [SerializeField] private float armScull = 25f;
    [SerializeField] private float elbowBend = 35f;
    [Tooltip("Swimming forward, the arms reach ahead")]
    [SerializeField] private float swimArmReach = 45f;

    [Header("Body")]
    [SerializeField] private float bob = 0.05f;
    [Tooltip("Forward lean swimming at full speed (degrees)")]
    [SerializeField] private float swimLean = 35f;
    [SerializeField] private float blendSpeed = 4f;

    float amount;
    float phase;
    float swimBlend;

    public override void ResetState() => amount = 0f;

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;
        amount = Mathf.MoveTowards(amount, s.Swimming ? 1f : 0f, blendSpeed * dt);
        if (amount <= 0f)
            return;

        float w = amount * amount * (3f - 2f * amount) * weight;
        Vector3 flat = new Vector3(s.Velocity.x, 0f, s.Velocity.z);
        float forward = Mathf.Clamp01(Vector3.Dot(flat, ctx.Forward) / Mathf.Max(swimSpeedReference, 0.1f));
        swimBlend = Mathf.Lerp(swimBlend, forward, Damp(4f, dt));
        phase = Mathf.Repeat(phase + Mathf.Lerp(treadRate, swimRate, swimBlend) * dt, 1f);
        float angle = phase * 2f * Mathf.PI;
        var rig = ctx.Rig;

        // Body: gentle bob, lean into forward swimming.
        Vector3 pelvis = new Vector3(0f, bob * Mathf.Sin(angle * 2f), 0f);
        pose.PelvisOffset = Vector3.Lerp(pose.PelvisOffset, pelvis, w);
        pose.PelvisRotation = Quaternion.Slerp(pose.PelvisRotation, Quaternion.Euler(swimLean * swimBlend, 0f, 0f), w);
        pose.ChestRotation = Quaternion.Slerp(pose.ChestRotation, Quaternion.Euler(-swimLean * 0.3f * swimBlend, 0f, 0f), w);

        for (int i = 0; i < 2; i++)
        {
            float side = i == CharacterPose.Left ? -1f : 1f;
            float a = angle + i * Mathf.PI;

            // Legs: each foot traces a circle under the hip (opposite to the other), trailing back
            // when swimming forward.
            Vector3 local = new Vector3(
                side * 0.3f,
                rig.SoleHeight + footLift + footCircle * Mathf.Sin(a),
                footCircle * Mathf.Cos(a) - swimKickBack * swimBlend);
            var foot = pose.Feet[i];
            foot.Position = Vector3.Lerp(foot.Position, ctx.Root + ctx.Facing * local, w);
            foot.Yaw = Mathf.LerpAngle(foot.Yaw, s.FacingYaw, w);
            foot.Pitch = Mathf.Lerp(foot.Pitch, 35f + 15f * Mathf.Cos(a), w);
            foot.Weight = Mathf.Max(foot.Weight, w);
            pose.Feet[i] = foot;

            // Arms: out to the side, sculling forward and back; reaching ahead when swimming.
            float scull = armScull * Mathf.Sin(angle) * side;
            Quaternion shoulder =
                Quaternion.AngleAxis(-swimArmReach * swimBlend, Vector3.right) *
                Quaternion.AngleAxis(scull, Vector3.up) *
                Quaternion.AngleAxis(side * armSpread, Vector3.forward);
            var arm = pose.Arms[i];
            arm.Shoulder = Quaternion.Slerp(arm.Shoulder, shoulder, w);
            arm.Elbow = Quaternion.Slerp(arm.Elbow, Quaternion.AngleAxis(-elbowBend, Vector3.right), w);
            pose.Arms[i] = arm;
        }
    }
}
