using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player input to character movement: camera relative walk/sprint, crouch and crawl, slope aware
/// speed (slower climbing hills), variable height jump with coyote time and input buffering, and
/// which way the body faces. Physics is done by <see cref="VoxelCharacterMotor"/>; animation reads
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
    [SerializeField] private float crawlSpeed = 1.3f;

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
    [SerializeField] private float crawlTurnSmoothTime = 0.3f;
    [Tooltip("First person, standing still: the body turns to follow the camera once the look angle passes this")]
    [SerializeField] private float idleTurnAngle = 70f;

    [Header("Crouch and Crawl")]
    [SerializeField] private float crouchTransitionSpeed = 6f;
    [Tooltip("Body height crouched: under 2 fits through two block high gaps")]
    [SerializeField] private float crouchHeight = 1.9f;
    [Tooltip("Toggles crawling (lying flat)")]
    [SerializeField] private Key crawlKey = Key.Z;
    [Tooltip("Body height crawling: under 1 fits through one block high gaps")]
    [SerializeField] private float crawlHeight = 0.95f;
    [Tooltip("Crawling, anything lower than this is crawled over; higher blocks (up to one) are climbed onto")]
    [SerializeField] private float crawlStepClearance = 0.5f;
    [SerializeField] private float crawlClimbHeight = 1.05f;

    [Header("Water")]
    [SerializeField] private float swimSpeed = 2.5f;
    [SerializeField] private float swimSprintSpeed = 4.5f;
    [SerializeField] private float waterAcceleration = 6f;
    [Tooltip("Speed multiplier wading in water up to swimming depth")]
    [SerializeField] private float wadeSpeedMultiplier = 0.55f;
    [Tooltip("Jumping from the surface (to climb out) works while the water is no deeper than this above the feet")]
    [SerializeField] private float surfaceJumpDepth = 2.4f;

    [Header("Dev: Noclip")]
    [Tooltip("Fly through everything (editor and development builds only)")]
    [SerializeField] private Key noclipKey = Key.N;
    [SerializeField] private float flySpeed = 15f;
    [SerializeField] private float flyFastMultiplier = 4f;

    [Header("References")]
    [SerializeField] private PlayerCamera playerCamera;
    [SerializeField] private PlayerInput playerInput;

    public event Action<float> Landed;
    public event Action Jumped;

    public enum Posture { Standing, Crouching, Crawling }

    private VoxelCharacterMotor motor;
    private InputAction moveAction, jumpAction, sprintAction, crouchAction;

    // Input
    private Vector2 moveInput;
    private bool sprintHeld;
    private bool crouchHeld;
    private bool jumpHeld;
    private bool crawlToggled;
    private float lastJumpPressedTime = float.NegativeInfinity;

    // State
    private Vector3 wishDirection;
    private Vector3 desiredVelocity;
    private bool isJumping;
    private float jumpStartTime = float.NegativeInfinity;
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
    public bool IsSprinting => posture == Posture.Standing && sprintHeld && moveInput.sqrMagnitude > 0.09f && crouch < 0.5f && (IsThirdPerson || moveInput.y > 0.3f);
    public bool IsNoClip => motor != null && motor.NoClip;
    public float FacingYaw => facingYaw;
    /// <summary>0 = sprint just started .. 1 = at top sprint speed.</summary>
    public float SprintMomentum => sprintMomentum;
    /// <summary>The posture the body is actually in (a low ceiling can keep it down).</summary>
    public Posture CurrentPosture => posture;

    Posture posture;
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
        motor.EnteredWater += OnMotorEnteredWater;
    }

    void OnDisable()
    {
        isJumping = false;
        motor.HorizontalVelocity = Vector3.zero;
        motor.Landed -= OnMotorLanded;
        motor.Jumped -= OnMotorJumped;
        motor.EnteredWater -= OnMotorEnteredWater;
    }

    void OnMotorLanded(float impact)
    {
        isJumping = false;
        Landed?.Invoke(impact);
    }

    void OnMotorJumped() => Jumped?.Invoke();

    // A jump that ends in water is over.
    void OnMotorEnteredWater() => isJumping = false;

    void Update()
    {
        ReadInput();
        UpdateWishDirection();
        UpdateFacing(Time.deltaTime);
        UpdatePosture();
        crouch = Mathf.MoveTowards(crouch, posture == Posture.Crouching ? 1f : 0f, crouchTransitionSpeed * Time.deltaTime);

        if ((Application.isEditor || Debug.isDebugBuild) && Keyboard.current != null && Keyboard.current[noclipKey].wasPressedThisFrame)
        {
            motor.NoClip = !motor.NoClip;
            motor.FlyVelocity = Vector3.zero;
        }
    }

    void ReadInput()
    {
        moveInput = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
        sprintHeld = sprintAction != null && sprintAction.IsPressed();
        crouchHeld = crouchAction != null && crouchAction.IsPressed();
        jumpHeld = jumpAction != null && jumpAction.IsPressed();

        if (Keyboard.current != null && Keyboard.current[crawlKey].wasPressedThisFrame)
            crawlToggled = !crawlToggled;

        if (jumpAction != null)
        {
            if (jumpAction.WasPressedThisFrame())
            {
                // Jump gets up from crawling instead of jumping.
                if (crawlToggled || posture == Posture.Crawling) crawlToggled = false;
                else lastJumpPressedTime = Time.time;
            }
            if (jumpAction.WasReleasedThisFrame() && isJumping)
                motor.ScaleUpwardVelocity(jumpCutMultiplier);
        }
        // Crouch while crawling gets up into a crouch.
        if (crawlToggled && crouchAction != null && crouchAction.WasPressedThisFrame())
            crawlToggled = false;
    }

    // Asks the body for the size of the wanted posture; it only grows when there is room, so a
    // low ceiling keeps the character crouched or crawling until it is clear.
    void UpdatePosture()
    {
        if (motor.IsSwimming || motor.NoClip)
            crawlToggled = false;

        var s = motor.Settings;
        Posture wanted = crawlToggled ? Posture.Crawling : crouchHeld && !motor.IsSwimming && !motor.NoClip ? Posture.Crouching : Posture.Standing;
        // Getting up from a crawl under a low ceiling: at least come up to a crouch if that fits.
        if (wanted == Posture.Standing && posture == Posture.Crawling && !motor.CanFit(s.height, s.stepClearance))
            wanted = Posture.Crouching;
        switch (wanted)
        {
            case Posture.Crawling: motor.RequestShape(crawlHeight, crawlStepClearance, crawlClimbHeight); break;
            case Posture.Crouching: motor.RequestShape(crouchHeight, s.stepClearance, 0f); break;
            default: motor.RequestShape(s.height, s.stepClearance, 0f); break;
        }

        float h = motor.BodyHeight;
        posture = h <= crawlHeight + 0.01f ? Posture.Crawling : h < s.height - 0.01f ? Posture.Crouching : Posture.Standing;
    }

    /// <summary>
    /// True on the frame any movement command is pressed (move, jump, crouch, sprint, crawl).
    /// Works while this component is disabled, e.g. to get up from the ragdoll.
    /// </summary>
    public bool AnyCommandPressed()
    {
        bool Pressed(InputAction a) => a != null && a.WasPressedThisFrame();
        return Pressed(moveAction) || Pressed(jumpAction) || Pressed(crouchAction) || Pressed(sprintAction) ||
               (Keyboard.current != null && Keyboard.current[crawlKey].wasPressedThisFrame);
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

        if (motor.NoClip)
        {
            UpdateNoClip();
            return;
        }
        if (motor.IsSwimming)
        {
            UpdateSwimming(dt);
            return;
        }
        motor.SwimVertical = 0f;

        bool grounded = motor.IsGrounded;
        if (grounded)
            lastGroundedTime = Time.time;

        // Back on the ground however we got there (landing, wading out, teleport): not jumping.
        if (isJumping && grounded && motor.Velocity.y <= 0f && Time.time - jumpStartTime > 0.2f)
            isJumping = false;

        // Hills: slower climbing, slightly slower descending.
        float rawSlope = wishDirection.sqrMagnitude > 0.01f ? motor.MeasureSlope(wishDirection) : 0f;
        slope = Mathf.Lerp(slope, rawSlope, 1f - Mathf.Exp(-slopeResponse * dt));
        float slopeMultiplier = slope >= 0f
            ? Mathf.Max(0.3f, Mathf.LerpUnclamped(1f, uphillSpeedMultiplier, Mathf.Min(slope, 1.5f)))
            : Mathf.Lerp(1f, downhillSpeedMultiplier, -slope);

        UpdateSprintMomentum(dt, grounded);

        float maxSpeed = posture == Posture.Crawling ? crawlSpeed
            : crouch > 0.5f ? crouchSpeed
            : IsSprinting ? Mathf.Lerp(sprintSpeed, Mathf.Max(sprintSpeed, topSprintSpeed), sprintMomentum)
            : walkSpeed;
        // Wading: slower the deeper the water.
        if (grounded && motor.WaterDepth > 0f)
            maxSpeed *= Mathf.Lerp(1f, wadeSpeedMultiplier, Mathf.Clamp01(motor.WaterDepth / motor.Settings.swimDepth));
        Vector3 target = wishDirection * (maxSpeed * slopeMultiplier);
        desiredVelocity = target;

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
        HandleJump(grounded);
    }

    void HandleJump(bool grounded)
    {
        bool jumpBuffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canJump = !isJumping && posture == Posture.Standing && crouch < 0.5f && (grounded || Time.time - lastGroundedTime <= coyoteTime);
        if (jumpBuffered && canJump)
        {
            motor.Jump(Mathf.Sqrt(2f * -Physics.gravity.y * jumpHeight));
            isJumping = true;
            jumpStartTime = Time.time;
            lastJumpPressedTime = float.NegativeInfinity;
            lastGroundedTime = float.NegativeInfinity;
        }
    }

    /// <summary>
    /// Hands the body back moving at <paramref name="velocity"/> (unfolding or getting up from the
    /// ragdoll mid-roll), facing the way it is going.
    /// </summary>
    public void CarryMomentum(Vector3 velocity)
    {
        motor.SetVelocity(velocity);
        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude > 1f) // moving: face that way (at rest keep facing where the head points)
            SetFacing(Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg);
    }

    // ----------------------------------------------------------------------------------
    // Water and noclip
    // ----------------------------------------------------------------------------------

    // Swim at the surface; Jump swims up (or, at the surface, jumps out), Crouch dives.
    void UpdateSwimming(float dt)
    {
        float speed = sprintHeld ? swimSprintSpeed : swimSpeed;
        Vector3 current = motor.Velocity;
        current.y = 0f;
        desiredVelocity = wishDirection * speed;
        motor.HorizontalVelocity = Vector3.MoveTowards(current, desiredVelocity, waterAcceleration * dt);
        motor.SwimVertical = (jumpHeld ? 1f : 0f) - (crouchHeld ? 1f : 0f);

        bool jumpBuffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        if (jumpBuffered && motor.WaterDepth <= surfaceJumpDepth)
        {
            // Always full height (releasing the button doesn't cut it), so a tap climbs out.
            motor.Jump(Mathf.Sqrt(2f * -Physics.gravity.y * jumpHeight));
            isJumping = false;
            lastJumpPressedTime = float.NegativeInfinity;
        }
        sprintMomentum = 0f;
    }

    // Fly where the camera looks; Jump / Crouch go straight up / down; Sprint is fast.
    void UpdateNoClip()
    {
        Quaternion look = playerCamera != null ? Quaternion.Euler(playerCamera.Pitch, playerCamera.Yaw, 0f) : Quaternion.Euler(0f, CameraYaw, 0f);
        Vector3 move = look * new Vector3(moveInput.x, 0f, moveInput.y) + Vector3.up * ((jumpHeld ? 1f : 0f) - (crouchHeld ? 1f : 0f));
        motor.FlyVelocity = Vector3.ClampMagnitude(move, 1f) * flySpeed * (sprintHeld ? flyFastMultiplier : 1f);
        desiredVelocity = motor.FlyVelocity;
        lastJumpPressedTime = float.NegativeInfinity;
        isJumping = false;
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

        if (motor.NoClip)
        {
            idleTurning = false;
            target = cameraYaw;
        }
        else if (moving)
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

        float smoothTime = posture == Posture.Crawling ? crawlTurnSmoothTime : IsSprinting ? sprintTurnSmoothTime : turnSmoothTime;
        facingYaw = Mathf.SmoothDampAngle(facingYaw, target, ref facingVelocity, smoothTime, Mathf.Infinity, dt);
    }

    public LocomotionState GetLocomotionState()
    {
        Vector3 look = playerCamera != null ? playerCamera.LookDirection : Quaternion.Euler(0f, facingYaw, 0f) * Vector3.forward;
        return new LocomotionState
        {
            Velocity = motor.Velocity,
            DesiredVelocity = desiredVelocity,
            Grounded = motor.IsGrounded,
            GroundHeight = motor.GroundHeight,
            AirTime = motor.AirTime,
            FacingYaw = facingYaw,
            LookDirection = look,
            WalkSpeed = walkSpeed,
            RunSpeed = sprintSpeed,
            SprintSpeed = Mathf.Max(sprintSpeed, topSprintSpeed),
            Sprinting = IsSprinting,
            Swimming = motor.IsSwimming,
            WaterDepth = motor.WaterDepth,
            Crouch = crouch,
            Crawling = posture == Posture.Crawling,
            BodyHeight = motor.BodyHeight,
            Slope = slope,
        };
    }
}
