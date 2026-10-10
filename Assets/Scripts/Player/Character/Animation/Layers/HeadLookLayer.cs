using UnityEngine;

/// <summary>Turns the head toward where the character is looking, within neck limits.</summary>
public class HeadLookLayer : CharacterAnimLayer
{
    [SerializeField] private float maxYaw = 70f;
    [SerializeField] private float maxPitchUp = 45f;
    [SerializeField] private float maxPitchDown = 55f;
    [Tooltip("Looking further behind than this, the head faces forward instead of twisting")]
    [SerializeField] private float lookBehindAngle = 120f;
    [SerializeField] private float smoothTime = 0.08f;

    float yaw, yawVelocity;
    float pitch, pitchVelocity;

    public override void Evaluate(CharacterPose pose, CharacterAnimContext ctx)
    {
        Vector3 look = Quaternion.Inverse(ctx.Facing) * ctx.State.LookDirection;
        if (look.sqrMagnitude < 1e-6f)
            look = Vector3.forward;
        look.Normalize();

        float targetYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
        float targetPitch = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg; // positive = looking down

        if (Mathf.Abs(targetYaw) > lookBehindAngle)
            targetYaw = 0f;
        targetYaw = Mathf.Clamp(targetYaw, -maxYaw, maxYaw);
        targetPitch = Mathf.Clamp(targetPitch, -maxPitchUp, maxPitchDown);

        yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVelocity, smoothTime, Mathf.Infinity, ctx.DeltaTime);
        pitch = Mathf.SmoothDampAngle(pitch, targetPitch, ref pitchVelocity, smoothTime, Mathf.Infinity, ctx.DeltaTime);

        Quaternion head = Quaternion.Euler(pitch, yaw, 0f);
        pose.HeadRotation = Quaternion.Slerp(Quaternion.identity, head, weight) * pose.HeadRotation;
    }
}
