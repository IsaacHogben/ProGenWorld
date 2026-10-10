using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural animation for a jointed character. Each frame it reads the body's state from an
/// <see cref="ICharacterLocomotionSource"/> (e.g. PlayerController), runs every enabled
/// <see cref="CharacterAnimLayer"/> on this object in order to build a <see cref="CharacterPose"/>,
/// and applies that pose to the <see cref="CharacterRig"/>.
/// </summary>
[DefaultExecutionOrder(50)]
public class CharacterAnimator : MonoBehaviour
{
    [SerializeField] private CharacterRig rig = new CharacterRig();
    [Tooltip("Find missing bone references by name on start")]
    [SerializeField] private bool autoBindBones = true;
    [Tooltip("Turn knees outward when they would go into blocks (high steps)")]
    [SerializeField] private bool kneesAvoidTerrain = true;

    private readonly List<CharacterAnimLayer> layers = new List<CharacterAnimLayer>();
    private readonly CharacterPose pose = new CharacterPose();
    private readonly CharacterAnimContext context = new CharacterAnimContext();
    private ICharacterLocomotionSource source;

    public CharacterRig Rig => rig;
    public CharacterPose Pose => pose;
    public ICharacterLocomotionSource Source => source;

    void Awake()
    {
        source = GetComponent<ICharacterLocomotionSource>();

        if (autoBindBones)
            rig.AutoBind(transform);
        rig.CaptureRest(transform);
        if (!rig.IsValid)
            Debug.LogWarning($"{name}: CharacterAnimator could not find the model root / pelvis bones.", this);

        if (kneesAvoidTerrain)
            rig.IsBlocked = p => VoxelPhysics.IsSolid(new Unity.Mathematics.int3(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));

        context.Rig = rig;
        context.Source = source;
    }

    void Start()
    {
        FillContext(0f);
        RefreshLayers();
    }

    /// <summary>Re-collects layer components (call after adding or removing one at runtime).</summary>
    public void RefreshLayers()
    {
        layers.Clear();
        GetComponents(layers);
        layers.Sort((a, b) => a.order.CompareTo(b.order));
        foreach (var layer in layers)
            layer.Initialize(this, context);
    }

    void FillContext(float dt)
    {
        var state = source != null ? source.GetLocomotionState() : new LocomotionState { Grounded = true, FacingYaw = transform.eulerAngles.y };
        context.State = state;
        context.DeltaTime = dt;
        context.Root = transform.position;
        context.Facing = Quaternion.Euler(0f, state.FacingYaw, 0f);
        context.Forward = context.Facing * Vector3.forward;
        context.Right = context.Facing * Vector3.right;
    }

    void LateUpdate()
    {
        if (!rig.IsValid)
            return;

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        FillContext(dt);
        pose.Reset(context.State.FacingYaw);

        foreach (var layer in layers)
        {
            if (layer != null && layer.isActiveAndEnabled && layer.weight > 0f)
                layer.Evaluate(pose, context);
        }

        rig.Apply(pose, transform, dt);
    }
}
