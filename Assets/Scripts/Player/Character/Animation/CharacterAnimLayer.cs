using UnityEngine;

/// <summary>Per-frame inputs shared by all animation layers.</summary>
public class CharacterAnimContext
{
    public LocomotionState State;
    public float DeltaTime;
    /// <summary>Rendered position of the character root (the feet pivot).</summary>
    public Vector3 Root;
    /// <summary>Facing rotation (yaw only).</summary>
    public Quaternion Facing;
    public Vector3 Forward;
    public Vector3 Right;
    public CharacterRig Rig;
    public ICharacterLocomotionSource Source;
}

/// <summary>
/// One procedural animation layer. Add layer components next to a CharacterAnimator; each frame
/// they run in ascending <see cref="order"/> and modify the shared <see cref="CharacterPose"/>,
/// which is then applied to the rig.
///
/// To add an animation (waving, holding a tool, climbing...), write a layer that blends its
/// changes into the pose by <see cref="weight"/> - e.g. set <c>pose.Arms[i].HandTarget</c> and
/// <c>HandWeight</c>, or multiply in a <c>Shoulder</c> rotation - and fade the weight in and out.
/// Layers later in the order see, and can override, what earlier layers produced.
/// </summary>
public abstract class CharacterAnimLayer : MonoBehaviour
{
    [Tooltip("How strongly this layer affects the pose")]
    [Range(0f, 1f)] public float weight = 1f;
    [Tooltip("Layers run in ascending order")]
    public int order;

    protected CharacterAnimator Animator { get; private set; }

    /// <summary>Called once by the animator after the rig's rest pose is captured.</summary>
    public virtual void Initialize(CharacterAnimator animator, CharacterAnimContext context)
    {
        Animator = animator;
    }

    public abstract void Evaluate(CharacterPose pose, CharacterAnimContext context);

    /// <summary>Exponential smoothing factor for a rate (1/s), frame-rate independent.</summary>
    protected static float Damp(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);
}
