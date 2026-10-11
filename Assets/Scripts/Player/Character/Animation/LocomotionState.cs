using System;
using UnityEngine;

/// <summary>Snapshot of what a character's body is doing, read by the animation layers each frame.</summary>
public struct LocomotionState
{
    /// <summary>World velocity of the body (horizontal part is what the feet walk with).</summary>
    public Vector3 Velocity;
    public bool Grounded;
    /// <summary>Height of the ground under the feet (last known while airborne).</summary>
    public float GroundHeight;
    public float AirTime;
    /// <summary>World yaw (degrees) the body should face.</summary>
    public float FacingYaw;
    /// <summary>World direction the character is looking (head aims here).</summary>
    public Vector3 LookDirection;
    /// <summary>Reference speeds the gait blends between (walk, run, full sprint).</summary>
    public float WalkSpeed;
    public float RunSpeed;
    public float SprintSpeed;
    public bool Sprinting;
    /// <summary>Sliding on the ground (crouch pressed while sprinting).</summary>
    public bool Sliding;
    /// <summary>Floating in deep water.</summary>
    public bool Swimming;
    /// <summary>Height of the water surface above the feet (0 or less out of water).</summary>
    public float WaterDepth;
    /// <summary>0 = standing, 1 = fully crouched.</summary>
    public float Crouch;
    /// <summary>Rise per metre of the ground ahead (1 = 45 degree stairs, negative = downhill).</summary>
    public float Slope;
}

/// <summary>
/// Anything that drives a character body (player input, AI) and can be animated by CharacterAnimator.
/// </summary>
public interface ICharacterLocomotionSource
{
    LocomotionState GetLocomotionState();

    /// <summary>Fired on landing with the downward impact speed.</summary>
    event Action<float> Landed;
    event Action Jumped;
}
