using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player input to character movement: camera relative walk/sprint/crouch, slope aware speed
/// (slower climbing hills), variable height jump with coyote time and input buffering, and which
/// way the body faces. Physics is done by <see cref="VoxelCharacterMotor"/>; animation reads
/// this through <see cref="ICharacterLocomotionSource"/>.
/// </summary>
[DefaultExecutionOrder(-60)]
[RequireComponent(typeof(VoxelCharacterMotor))]
public class PlayerController : MonoBehaviour, ICharacterLocomotionSource
{
    [Header("Speed")]
    [SerializeField] private float walkSpeed = 4f;
    [Tooltip("Speed when the sprint key is first held (a run)")]
    [SerializeField] private float sprintSpeed = 7.5f;
    [SerializeField] private float crouchSpeed = 2f;

    [Header("Sprint Momentum")]
    [Tooltip("Top speed reached by sprinting without interruption")]
    [SerializeField] private float topSprintSpeed = 11f;
    [Tooltip("Seconds of uninterrupted sprinting to build up to top speed")]
    [SerializeField] private float sprintBuildUpTime = 1.6f;
    [Tooltip("Momentum lost each time the ground steps up or down by a block")]
    [Range(0f, 1f)] [SerializeField] private float momentumLossPerStep = 0.3f;
    [Tooltip("Momentum lost per second while not sprinting, turning hard or climbing")]
    [SerializeField] private float momentumDecay = 2f;
    [Tooltip("Slopes steeper than this (rise per metre) stop momentum building")]
    [SerializeField] private float maxBuildUpSlope = 0.35f;
    [Tooltip("Turning faster than this (degrees per second) stops momentum building")]
    [SerializeField] private float maxBuildUpTurnRate = 120f;

    [Header("Acceleration")]
    [SerializeField] private float groundAcceleration = 30f;
    [SerializeField] private float groundDeceleration = 40f;
    [SerializeField] private float airAcceleration = 8f;

    [Header("Hills")]
    [Tooltip("Speed multiplier climbing 45 degree block stairs (one block up per block forward). Steeper slows more, gentler less")]
    [SerializeField] private float uphillSpeedMultiplier = 0.65f;
    [Tooltip("Speed multiplier going down 45 degree block stairs")]
    [SerializeField] private float downhillSpeedMultiplier = 0.45f;
    [Tooltip("How quickly speed adapts to the slope (1/s)")]
    [SerializeField] private float slopeResponse = 6f;

    [Header("Jump")]
    [SerializeField] private float jumpHeight = 2.3f;
    [Tooltip("Time after leaving ground where jump is still allowed")]
    [SerializeField] private float coyoteTime = 0.12f;
    [Tooltip("A jump pressed this long before landing still happens on landing")]
    [SerializeField] private float jumpBufferTime = 0.15f;
    [Tooltip("Upward velocity kept when the jump button is released early")]
    [Range(0f, 1f)]
    [SerializeField] private float jumpCutMultiplier = 0.5f;

    [Header("Facing")]
    [Tooltip("Time to turn toward the movement direction")]
    [SerializeField] private float turnSmoothTime = 0.1f;
    [SerializeField] private float sprintTurnSmoothTime = 0.16f;
    [Tooltip("First person, standing still: the body turns to follow the camera once the look angle passes this")]
    [SerializeField] private float idleTurnAngle = 70f;

    [Header("Crouch")]
    [SerializeField] private float crouchTransitionSpeed = 6f;

    [Header("References")]
    [SerializeField] private PlayerCamera playerCamera;
    [SerializeField] private PlayerInput playerInput;

    public event Action<float> Landed;
    public event Action Jumped;

    private VoxelCharacterMotor motor;
    private InputAction moveAction, jumpAction, sprintAction, crouchAction;

    // Input
    private Vector2 moveInput;
    private bool sprintHeld;
    private bool crouchHeld;
    private float lastJumpPressedTime = float.NegativeInfinity;

    // State
    private Vector3 wishDirection;
    private bool isJumping;
    private float lastGroundedTime = float.NegativeInfinity;
    private float facingYaw;
    private float facingVelocity;
    private bool idleTurning;
    private float slope;
    private float crouch;
    private float sprintMomentum;
    private float lastGroundHeight;
    private float lastWishYaw;

    public VoxelCharacterMotor Motor => motor;
    public bool IsSprinting => sprintHeld && moveInput.sqrMagnitude > 0.09f && crouch < 0.5f && (IsThirdPerson || moveInput.y > 0.3f);
    public float FacingYaw => facingYaw;
    /// <summary>0 = sprint just started .. 1 = at top sprint speed.</summary>
    public float SprintMomentum => sprintMomentum;

    bool IsThirdPerson => playerCamera == null || playerCamera.IsThirdPerson;

    void Awake()
    {
        motor = GetComponent<VoxelCharacterMotor>();
        if (playerInput == null) playerInput = GetComponent<PlayerInput>();
        if (playerCamera == null) playerCamera = GetComponent<PlayerCamera>();

        if (playerInput != null)
        {
            moveAction = playerInput.actions.FindAction("Move");
            jumpAction = playerInput.actions.FindAction("Jump");
            sprintAction = playerInput.actions.FindAction("Sprint");
            crouchAction = playerInput.actions.FindAction("Crouch");
        }

        facingYaw = transform.eulerAngles.y;
    }

    void OnEnable()
    {
        motor.Landed += OnMotorLanded;
        motor.Jumped += OnMotorJumped;
    }

    void OnDisable()
    {
        motor.Landed -= OnMotorLanded;
        motor.Jumped -= OnMotorJumped;
    }

    void OnMotorLanded(float impact)
    {
        isJumping = false;
        Landed?.Invoke(impact);
    }

    void OnMotorJumped() => Jumped?.Invoke();

    void Update()
    {
        ReadInput();
        UpdateWishDirection();
        UpdateFacing(Time.deltaTime);
        crouch = Mathf.MoveTowards(crouch, crouchHeld ? 1f : 0f, crouchTransitionSpeed * Time.deltaTime);
    }

    void ReadInput()
    {
        moveInput = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
        sprintHeld = sprintAction != null && sprintAction.IsPressed();
        crouchHeld = crouchAction != null && crouchAction.IsPressed();

        if (jumpAction != null)
        {
            if (jumpAction.WasPressedThisFrame())
                lastJumpPressedTime = Time.time;
            if (jumpAction.WasReleasedThisFrame() && isJumping)
                motor.ScaleUpwardVelocity(jumpCutMultiplier);
        }
    }

    float CameraYaw => playerCamera != null ? playerCamera.Yaw : transform.eulerAngles.y;

    void UpdateWishDirection()
    {
        Quaternion yaw = Quaternion.Euler(0f, CameraYaw, 0f);
        wishDirection = yaw * new Vector3(moveInput.x, 0f, moveInput.y);
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        UpdateWishDirection();

        bool grounded = motor.IsGrounded;
        if (grounded)
            lastGroundedTime = Time.time;

        // Hills: slower climbing, slightly slower descending.
        float rawSlope = wishDirection.sqrMagnitude > 0.01f ? motor.MeasureSlope(wishDirection) : 0f;
        slope = Mathf.Lerp(slope, rawSlope, 1f - Mathf.Exp(-slopeResponse * dt));
        float slopeMultiplier = slope >= 0f
            ? Mathf.Max(0.3f, Mathf.LerpUnclamped(1f, uphillSpeedMultiplier, Mathf.Min(slope, 1.5f)))
            : Mathf.Lerp(1f, downhillSpeedMultiplier, -slope);

        UpdateSprintMomentum(dt, grounded);

        float maxSpeed = crouch > 0.5f ? crouchSpeed
            : IsSprinting ? Mathf.Lerp(sprintSpeed, Mathf.Max(sprintSpeed, topSprintSpeed), sprintMomentum)
            : walkSpeed;
        Vector3 target = wishDirection * (maxSpeed * slopeMultiplier);

        // Start from what the body actually did, so walls don't store phantom speed.
        Vector3 current = motor.Velocity;
        current.y = 0f;

        float accel;
        if (!grounded)
            accel = airAcceleration;
        else if (target.sqrMagnitude >= current.sqrMagnitude && Vector3.Dot(target, current) >= 0f)
            accel = groundAcceleration;
        else
            accel = groundDeceleration;

        motor.HorizontalVelocity = Vector3.MoveTowards(current, target, accel * dt);

        bool jumpBuffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canJump = !isJumping && crouch < 0.5f && (grounded || Time.time - lastGroundedTime <= coyoteTime);
        if (jumpBuffered && canJump)
        {
            motor.Jump(Mathf.Sqrt(2f * -Physics.gravity.y * jumpHeight));
            isJumping = true;
            lastJumpPressedTime = float.NegativeInfinity;
            lastGroundedTime = float.NegativeInfinity;
        }
    }

    // Holding sprint on open ground builds momentum toward top speed; stepping up or down blocks,
    // hills, hard turns, walls and letting go of sprint take it away.
    void UpdateSprintMomentum(float dt, bool grounded)
    {
        float wishYaw = Mathf.Atan2(wishDirection.x, wishDirection.z) * Mathf.Rad2Deg;
        float turnRate = Mathf.Abs(Mathf.DeltaAngle(lastWishYaw, wishYaw)) / dt;
        lastWishYaw = wishYaw;

        if (!IsSprinting || motor.HitWall)
        {
            sprintMomentum = Mathf.MoveTowards(sprintMomentum, 0f, momentumDecay * dt * (motor.HitWall ? 4f : 1f));
        }
        else if (grounded)
        {
            if (Mathf.Abs(motor.GroundHeight - lastGroundHeight) > 0.5f)
                sprintMomentum = Mathf.Max(0f, sprintMomentum - momentumLossPerStep);

            bool open = Mathf.Abs(slope) <= maxBuildUpSlope && turnRate <= maxBuildUpTurnRate;
            sprintMomentum = open
                ? Mathf.MoveTowards(sprintMomentum, 1f, dt / Mathf.Max(0.05f, sprintBuildUpTime))
                : Mathf.MoveTowards(sprintMomentum, 0f, momentumDecay * dt);
        }
        // In the air (jumping while sprinting) momentum is kept.

        if (grounded)
            lastGroundHeight = motor.GroundHeight;
    }

    /// <summary>Turns the body to face <paramref name="yaw"/> immediately (after a teleport, getting up...).</summary>
    public void SetFacing(float yaw)
    {
        facingYaw = yaw;
        facingVelocity = 0f;
    }

    void UpdateFacing(float dt)
    {
        float cameraYaw = CameraYaw;
        bool moving = wishDirection.sqrMagnitude > 0.01f;
        float target = facingYaw;

        if (moving)
        {
            idleTurning = false;
            target = IsThirdPerson
                ? Mathf.Atan2(wishDirection.x, wishDirection.z) * Mathf.Rad2Deg
                : cameraYaw;
        }
        else if (!IsThirdPerson)
        {
            // First person: turn in place once the head has turned too far.
            float diff = Mathf.Abs(Mathf.DeltaAngle(facingYaw, cameraYaw));
            if (diff > idleTurnAngle) idleTurning = true;
            else if (diff < 3f) idleTurning = false;
            if (idleTurning) target = cameraYaw;
        }
        else
        {
            idleTurning = false;
        }

        float smoothTime = IsSprinting ? sprintTurnSmoothTime : turnSmoothTime;
        facingYaw = Mathf.SmoothDampAngle(facingYaw, target, ref facingVelocity, smoothTime, Mathf.Infinity, dt);
    }

    public LocomotionState GetLocomotionState()
    {
        Vector3 look = playerCamera != null ? playerCamera.LookDirection : Quaternion.Euler(0f, facingYaw, 0f) * Vector3.forward;
        return new LocomotionState
        {
            Velocity = motor.Velocity,
            Grounded = motor.IsGrounded,
            GroundHeight = motor.GroundHeight,
            AirTime = motor.AirTime,
            FacingYaw = facingYaw,
            LookDirection = look,
            WalkSpeed = walkSpeed,
            RunSpeed = sprintSpeed,
            SprintSpeed = Mathf.Max(sprintSpeed, topSprintSpeed),
            Sprinting = IsSprinting,
            Crouch = crouch,
            Slope = slope,
        };
    }
}
