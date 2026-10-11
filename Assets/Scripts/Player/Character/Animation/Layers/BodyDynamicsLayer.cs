using UnityEngine;

/// <summary>
/// Gives the body weight: leans into acceleration and speed, banks into turns, leans into hills,
/// counter-twists the chest against the stride, and pitches with the jump arc.
/// </summary>
public class BodyDynamicsLayer : CharacterAnimLayer
{
    [Header("Lean")]
    [Tooltip("Forward lean per m/s^2 of acceleration (degrees)")]
    [SerializeField] private float accelerationLean = 1.6f;
    [SerializeField] private float maxAccelerationLean = 14f;
    [Tooltip("Forward lean at run speed (degrees)")]
    [SerializeField] private float runLean = 6f;
    [Tooltip("Extra forward lean at full sprint (degrees)")]
    [SerializeField] private float sprintLean = 5f;
    [Tooltip("Forward lean per unit of slope going uphill, back lean downhill (degrees)")]
    [SerializeField] private float slopeLean = 7f;
    [Tooltip("Smoothing time of the lean (seconds)")]
    [SerializeField] private float leanSmoothTime = 0.12f;
    [Tooltip("Share of the lean taken by the pelvis (the chest takes the rest)")]
    [Range(0f, 1f)] [SerializeField] private float pelvisShare = 0.25f;

    [Header("Turning")]
    [Tooltip("Bank into turns per m/s^2 of sideways acceleration (degrees)")]
    [SerializeField] private float bankPerAcceleration = 1.3f;
    [SerializeField] private float maxBank = 12f;

    [Header("Stride")]
    [Tooltip("How much of the stride twist reaches the hips (0 = hips stay square)")]
    [Range(0f, 1f)] [SerializeField] private float pelvisTwistShare = 0.2f;
    [Tooltip("Chest twist against the legs when walking / running (degrees)")]
    [SerializeField] private float walkTwist = 4f;
    [SerializeField] private float runTwist = 9f;
    [SerializeField] private float sprintTwist = 12f;

    [Header("Air")]
    [Tooltip("Lean when rising (negative = back) and when falling (degrees)")]
    [SerializeField] private float riseLean = -6f;
    [SerializeField] private float fallLean = 8f;

    Vector3 smoothedVelocity;
    Vector3 lastVelocity;
    Vector3 acceleration;
    float pitch, pitchVelocity;
    float roll, rollVelocity;

    public override void Initialize(CharacterAnimator animator, CharacterAnimContext context)
    {
        base.Initialize(animator, context);
        smoothedVelocity = lastVelocity = Flat(context.State.Velocity);
    }

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        float dt = ctx.DeltaTime;
        var s = ctx.State;

        // The body velocity changes in fixed steps; smooth it before differentiating.
        smoothedVelocity = Vector3.Lerp(smoothedVelocity, Flat(s.Velocity), Damp(12f, dt));
        Vector3 rawAccel = (smoothedVelocity - lastVelocity) / dt;
        lastVelocity = smoothedVelocity;
        acceleration = Vector3.Lerp(acceleration, rawAccel, Damp(10f, dt));

        float speed = smoothedVelocity.magnitude;
        float forwardFraction = speed > 0.1f ? Vector3.Dot(smoothedVelocity / speed, ctx.Forward) : 0f;
        float forwardAccel = Vector3.Dot(acceleration, ctx.Forward);
        float sideAccel = Vector3.Dot(acceleration, ctx.Right);
        float grounded = 1f - pose.Airborne;

        float targetPitch =
            Mathf.Clamp(forwardAccel * accelerationLean, -maxAccelerationLean, maxAccelerationLean) * grounded +
            (runLean * pose.RunBlend + sprintLean * pose.SprintBlend) * forwardFraction * grounded +
            slopeLean * Mathf.Clamp(s.Slope, -1f, 1.5f) * Mathf.Abs(forwardFraction) * grounded +
            Mathf.Lerp(fallLean, riseLean, Mathf.InverseLerp(-8f, 6f, s.Velocity.y)) * pose.Airborne;

        // Lean into the turn: up tilts toward the centre.
        float targetRoll = Mathf.Clamp(-sideAccel * bankPerAcceleration, -maxBank, maxBank) * Mathf.Lerp(1f, 0.4f, pose.Airborne);

        pitch = Mathf.SmoothDamp(pitch, targetPitch, ref pitchVelocity, leanSmoothTime, Mathf.Infinity, dt);
        roll = Mathf.SmoothDamp(roll, targetRoll, ref rollVelocity, leanSmoothTime, Mathf.Infinity, dt);

        float twist = Mathf.Lerp(Mathf.Lerp(walkTwist, runTwist, pose.RunBlend), sprintTwist, pose.SprintBlend) * pose.GaitWeight * Mathf.Sin(pose.GaitPhase * 2f * Mathf.PI);

        Quaternion pelvis = Quaternion.Euler(pitch * pelvisShare, -twist * pelvisTwistShare, roll * pelvisShare);
        Quaternion chest = Quaternion.Euler(pitch * (1f - pelvisShare), twist, roll * (1f - pelvisShare));

        pose.PelvisRotation = Quaternion.Slerp(Quaternion.identity, pelvis, weight) * pose.PelvisRotation;
        pose.ChestRotation = Quaternion.Slerp(Quaternion.identity, chest, weight) * pose.ChestRotation;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
