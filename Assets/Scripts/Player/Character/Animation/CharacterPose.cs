using UnityEngine;

/// <summary>Where a foot should be this frame.</summary>
public struct FootPose
{
    /// <summary>World position of the foot bone.</summary>
    public Vector3 Position;
    /// <summary>World yaw (degrees) the foot points.</summary>
    public float Yaw;
    /// <summary>Degrees, positive = toes down.</summary>
    public float Pitch;
    /// <summary>0 = leg follows the body (rest pose), 1 = foot reaches Position (IK).</summary>
    public float Weight;
    /// <summary>Direction this knee points, facing frame (zero = the pose's KneeDirection).</summary>
    public Vector3 Knee;
}

/// <summary>How an arm should be posed this frame.</summary>
public struct ArmPose
{
    /// <summary>Rotation added at the shoulder, in the character's facing frame.</summary>
    public Quaternion Shoulder;
    /// <summary>Rotation added at the elbow, in the character's facing frame.</summary>
    public Quaternion Elbow;
    /// <summary>World position the hand reaches for when HandWeight > 0 (for holding, pointing, climbing...).</summary>
    public Vector3 HandTarget;
    public float HandWeight;
}

/// <summary>
/// The pose that animation layers build up each frame before it is applied to the rig.
/// Additive rotations are expressed in the facing frame (x = right, y = up, z = forward), so
/// layers never need to know the model's bone axes.
/// </summary>
public class CharacterPose
{
    public const int Left = 0;
    public const int Right = 1;

    /// <summary>World yaw (degrees) the model faces.</summary>
    public float FacingYaw;
    /// <summary>Pelvis offset from its rest position, facing frame (metres).</summary>
    public Vector3 PelvisOffset;
    /// <summary>Rotation added to the pelvis (moves the whole body above the legs).</summary>
    public Quaternion PelvisRotation;
    /// <summary>Rotation added to the chest, on top of the pelvis.</summary>
    public Quaternion ChestRotation;
    /// <summary>Rotation added to the head, on top of the chest.</summary>
    public Quaternion HeadRotation;
    /// <summary>Direction the knees point, facing frame (forward by default).</summary>
    public Vector3 KneeDirection;

    public readonly FootPose[] Feet = new FootPose[2];
    public readonly ArmPose[] Arms = new ArmPose[2];

    // Shared timing written by the locomotion layer so other layers can sync to the steps.
    /// <summary>Gait cycle 0..1. The left foot touches down at 0, the right at 0.5.</summary>
    public float GaitPhase;
    /// <summary>0 standing still .. 1 walking or faster.</summary>
    public float GaitWeight;
    /// <summary>0 walking .. 1 running.</summary>
    public float RunBlend;
    /// <summary>0 running .. 1 full sprint (long bounding strides with time in the air).</summary>
    public float SprintBlend;
    /// <summary>0 grounded .. 1 fully in the air pose.</summary>
    public float Airborne;

    public void Reset(float facingYaw)
    {
        FacingYaw = facingYaw;
        PelvisOffset = Vector3.zero;
        PelvisRotation = Quaternion.identity;
        ChestRotation = Quaternion.identity;
        HeadRotation = Quaternion.identity;
        KneeDirection = Vector3.forward;
        for (int i = 0; i < 2; i++)
        {
            Feet[i] = new FootPose { Yaw = facingYaw };
            Arms[i] = new ArmPose { Shoulder = Quaternion.identity, Elbow = Quaternion.identity };
        }
    }
}
