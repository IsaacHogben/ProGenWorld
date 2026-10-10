using UnityEngine;

/// <summary>
/// Arms swing against the legs in time with the gait, pump with bent elbows when running, and
/// lift out for balance in the air.
/// </summary>
public class ArmSwingLayer : CharacterAnimLayer
{
    [Header("Swing (degrees)")]
    [SerializeField] private float walkSwing = 20f;
    [SerializeField] private float runSwing = 38f;
    [Tooltip("Flip if the arms swing with the leg on the same side instead of against it")]
    [SerializeField] private bool invertSwing = false;

    [Header("Elbows (degrees)")]
    [SerializeField] private float idleElbow = 6f;
    [SerializeField] private float walkElbow = 15f;
    [SerializeField] private float runElbow = 65f;
    [Tooltip("Extra bend while the arm is forward")]
    [SerializeField] private float forwardElbow = 12f;

    [Header("Spread (degrees)")]
    [SerializeField] private float walkSpread = 4f;
    [SerializeField] private float runSpread = 10f;

    [Header("Air (degrees)")]
    [SerializeField] private float airSpread = 35f;
    [SerializeField] private float airForward = 15f;
    [SerializeField] private float airElbow = 35f;

    [Tooltip("How quickly the arms follow (1/s)")]
    [SerializeField] private float responsiveness = 14f;

    readonly float[] swing = new float[2];
    readonly float[] elbow = new float[2];
    readonly float[] spread = new float[2];

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float k = Damp(responsiveness, ctx.DeltaTime);
        float gait = pose.GaitWeight;
        float run = pose.RunBlend;
        float air = pose.Airborne;

        for (int i = 0; i < 2; i++)
        {
            // Each leg is furthest forward at its touchdown (left at phase 0, right at 0.5);
            // the arm on the same side is then furthest back.
            float legForward = Mathf.Cos(Mathf.Repeat(pose.GaitPhase + 0.5f * i, 1f) * 2f * Mathf.PI);
            float armForward = invertSwing ? legForward : -legForward;

            float targetSwing = Mathf.Lerp(walkSwing, runSwing, run) * gait * armForward;
            float targetElbow = idleElbow + Mathf.Lerp(walkElbow, runElbow, run) * gait + Mathf.Max(0f, armForward) * forwardElbow * gait;
            float targetSpread = Mathf.Lerp(walkSpread, runSpread, run) * gait;

            targetSwing = Mathf.Lerp(targetSwing, airForward, air);
            targetElbow = Mathf.Lerp(targetElbow, airElbow, air);
            targetSpread = Mathf.Lerp(targetSpread, airSpread, air);

            swing[i] = Mathf.Lerp(swing[i], targetSwing, k);
            elbow[i] = Mathf.Lerp(elbow[i], targetElbow, k);
            spread[i] = Mathf.Lerp(spread[i], targetSpread, k);

            // Facing frame: rotating about +x swings a hanging arm backward, about +z swings it to -x.
            float side = i == CharacterPose.Left ? -1f : 1f;
            Quaternion shoulder = Quaternion.AngleAxis(side * spread[i], Vector3.forward) * Quaternion.AngleAxis(-swing[i], Vector3.right);
            Quaternion elbowRot = Quaternion.AngleAxis(-elbow[i], Vector3.right);

            var arm = pose.Arms[i];
            arm.Shoulder = Quaternion.Slerp(Quaternion.identity, shoulder, weight) * arm.Shoulder;
            arm.Elbow = Quaternion.Slerp(Quaternion.identity, elbowRot, weight) * arm.Elbow;
            pose.Arms[i] = arm;
        }
    }
}
