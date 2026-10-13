using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player input to character movement: camera relative walk/sprint/crouch, slide and dodge, slope
/// aware speed (slower climbing hills), variable height jump with coyote time and input
/// buffering, and which way the body faces. Physics is done by <see cref="VoxelCharacterMotor"/>; animation reads
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

    [Header("Slide (crouch while running)")]
    [Tooltip("Pressing crouch while moving at least this fast slides (walk speed and up). Slower, it crouches")]
    [SerializeField] private float slideMinSpeed = 3.5f;
    [Tooltip("Speed added when the slide starts")]
    [SerializeField] private float slideBoost = 2f;
    [Tooltip("A slide starts at least this fast, so sliding out of a run carries you")]
    [SerializeField] private float slideStartSpeed = 8f;
    [Tooltip("Seconds after a slide before the next one gets the start boost again")]
    [SerializeField] private float slideBoostCooldown = 1f;
    [Tooltip("Slowing on flat ground (m/s^2)")]
    [SerializeField] private float slideFriction = 3.5f;
    [Tooltip("How strongly slopes speed up (downhill) or slow (uphill) a slide. 1 = full gravity along the slope")]
    [SerializeField] private float slideGravity = 0.85f;
    [SerializeField] private float slideMaxSpeed = 16f;
    [Tooltip("The slide ends below this speed")]
    [SerializeField] private float slideEndSpeed = 2.5f;
    [Tooltip("Steering while sliding (degrees per second)")]
    [SerializeField] private float slideSteer = 50f;
    [Tooltip("Shortest slide, even if crouch is released straight away")]
    [SerializeField] private float slideMinTime = 0.35f;
    [Tooltip("Unfolding (or getting up) faster than this goes straight into a slide that carries the momentum")]
    [SerializeField] private float carrySlideMinSpeed = 3.5f;

    [Header("Dodge")]
    [SerializeField] private Key dodgeKey = Key.Q;
    [Tooltip("Distance covered by a dodge")]
    [SerializeField] private float dodgeDistance = 4f;
    [SerializeField] private float dodgeDuration = 0.4f;
    [Tooltip("Seconds after a dodge before the next one")]
    [SerializeField] private float dodgeCooldown = 0.4f;

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
    private bool slideRequested;
    private bool isSliding;
    private Vector3 slideDirection;
    private float slideSpeed;
    private float slideTime;
    private bool jumpHeld;
    private float jumpStartTime = float.NegativeInfinity;
    private float lastSlideEndTime = float.NegativeInfinity;
    private bool slideHeldWithoutCrouch;
    private bool dodgeRequested;
    private bool isDodging;
    private Vector3 dodgeDirection;
    private float dodgeTime;
    private float dodgeExitSpeed;
    private float lastDodgeEndTime = float.NegativeInfinity;

    public VoxelCharacterMotor Motor => motor;
    public bool IsSprinting => !isSliding && !isDodging && sprintHeld && moveInput.sqrMagnitude > 0.09f && crouch < 0.5f && (IsThirdPerson || moveInput.y > 0.3f);
    public bool IsSliding => isSliding;
    public bool IsDodging => isDodging;
    public bool IsNoClip => motor != null && motor.NoClip;
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
        motor.EnteredWater += OnMotorEnteredWater;
    }

    void OnDisable()
    {
        isSliding = false;
        isDodging = false;
        isJumping = false;
        slideRequested = dodgeRequested = false;
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
        bool crouchPose = crouchHeld && !isSliding && !motor.IsSwimming && !motor.NoClip;
        crouch = Mathf.MoveTowards(crouch, crouchPose ? 1f : 0f, crouchTransitionSpeed * Time.deltaTime);

        if ((Application.isEditor || Debug.isDebugBuild) && Keyboard.current != null && Keyboard.current[noclipKey].wasPressedThisFrame)
        {
            motor.NoClip = !motor.NoClip;
            motor.FlyVelocity = Vector3.zero;
            isSliding = false;
        }
    }

    void ReadInput()
    {
        moveInput = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
        sprintHeld = sprintAction != null && sprintAction.IsPressed();
        crouchHeld = crouchAction != null && crouchAction.IsPressed();
        jumpHeld = jumpAction != null && jumpAction.IsPressed();

        // Crouch pressed while running slides instead (checked against speed in FixedUpdate).
        if (crouchAction != null && crouchAction.WasPressedThisFrame() && moveInput.sqrMagnitude > 0.09f)
            slideRequested = true;
        if (Keyboard.current != null && Keyboard.current[dodgeKey].wasPressedThisFrame)
            dodgeRequested = true;

        if (jumpAction != null)
        {
            if (jumpAction.WasPressedThisFrame())
                lastJumpPressedTime = Time.time;
            if (jumpAction.WasReleasedThisFrame() && isJumping)
                motor.ScaleUpwardVelocity(jumpCutMultiplier);
        }
    }

    /// <summary>
    /// True on the frame any movement command is pressed (move, jump, crouch, sprint, dodge).
    /// Works while this component is disabled, e.g. to get up from the ragdoll.
    /// </summary>
    public bool AnyCommandPressed()
    {
        bool Pressed(InputAction a) => a != null && a.WasPressedThisFrame();
        return Pressed(moveAction) || Pressed(jumpAction) || Pressed(crouchAction) || Pressed(sprintAction) ||
               (Keyboard.current != null && Keyboard.current[dodgeKey].wasPressedThisFrame);
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
            isDodging = false;
            slideRequested = dodgeRequested = false;
            UpdateNoClip();
            return;
        }
        if (motor.IsSwimming)
        {
            isSliding = false;
            isDodging = false;
            slideRequested = dodgeRequested = false; // crouch dives here
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

        if (dodgeRequested)
        {
            dodgeRequested = false;
            TryStartDodge(grounded);
        }
        if (isDodging)
        {
            slideRequested = false;
            UpdateDodge(dt);
            HandleJump(grounded);
            return;
        }

        if (slideRequested)
        {
            slideRequested = false;
            TryStartSlide(grounded);
        }
        if (isSliding)
        {
            UpdateSlide(dt, grounded);
            HandleJump(grounded);
            return;
        }

        UpdateSprintMomentum(dt, grounded);

        float maxSpeed = crouch > 0.5f ? crouchSpeed
            : IsSprinting ? Mathf.Lerp(sprintSpeed, Mathf.Max(sprintSpeed, topSprintSpeed), sprintMomentum)
            : walkSpeed;
        // Wading: slower the deeper the water.
        if (grounded && motor.WaterDepth > 0f)
            maxSpeed *= Mathf.Lerp(1f, wadeSpeedMultiplier, Mathf.Clamp01(motor.WaterDepth / motor.Settings.swimDepth));
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
        HandleJump(grounded);
    }

    void HandleJump(bool grounded)
    {
        bool jumpBuffered = Time.time - lastJumpPressedTime <= jumpBufferTime;
        bool canJump = !isJumping && crouch < 0.5f && (grounded || Time.time - lastGroundedTime <= coyoteTime);
        if (jumpBuffered && canJump)
        {
            // Jumping out of a slide or dodge keeps its speed.
            if (isSliding) EndSlide();
            isDodging = false;
            motor.Jump(Mathf.Sqrt(2f * -Physics.gravity.y * jumpHeight));
            isJumping = true;
            jumpStartTime = Time.time;
            lastJumpPressedTime = float.NegativeInfinity;
            lastGroundedTime = float.NegativeInfinity;
        }
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
        motor.HorizontalVelocity = Vector3.MoveTowards(current, wishDirection * speed, waterAcceleration * dt);
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
        lastJumpPressedTime = float.NegativeInfinity;
        isJumping = false;
    }

    // ----------------------------------------------------------------------------------
    // Slide
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Hands the body back moving at <paramref name="velocity"/> (unfolding or getting up from the
    /// ragdoll mid-roll): fast enough on the ground it slides on with that speed until friction
    /// stops it; in the air the momentum carries the jump.
    /// </summary>
    public void CarryMomentum(Vector3 velocity, bool onGround)
    {
        motor.SetVelocity(velocity);
        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        if (flat.sqrMagnitude > 1f) // moving: face that way (at rest keep facing where the head points)
            SetFacing(Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg);
        if (!onGround || flat.magnitude < carrySlideMinSpeed)
            return;
        isSliding = true;
        slideHeldWithoutCrouch = true;
        slideDirection = flat.normalized;
        slideSpeed = Mathf.Min(flat.magnitude, slideMaxSpeed);
        slideTime = 0f;
    }

    void TryStartSlide(bool grounded)
    {
        Vector3 v = motor.Velocity;
        v.y = 0f;
        if (!grounded || v.magnitude < slideMinSpeed || crouch > 0.5f)
            return;
        isSliding = true;
        slideDirection = v.normalized;
        // A fresh slide kicks off fast; chaining slides doesn't keep adding speed.
        float start = v.magnitude;
        if (Time.time - lastSlideEndTime > slideBoostCooldown)
            start = Mathf.Max(start + slideBoost, slideStartSpeed);
        slideSpeed = Mathf.Min(start, slideMaxSpeed);
        slideTime = 0f;
    }

    void EndSlide()
    {
        isSliding = false;
        slideHeldWithoutCrouch = false;
        lastSlideEndTime = Time.time;
    }

    // Friction slows the slide; gravity along the slope speeds it up downhill and slows it uphill,
    // so a steep surface carries the character down. Steering is gentle.
    void UpdateSlide(float dt, bool grounded)
    {
        slideTime += dt;

        if (motor.HitWall)
        {
            Vector3 kept = motor.Velocity;
            kept.y = 0f;
            slideSpeed = kept.magnitude;
            if (slideSpeed > 0.1f) slideDirection = kept / slideSpeed;
        }

        if (grounded)
        {
            float rise = motor.MeasureSlope(slideDirection);
            float downhill = -rise / Mathf.Sqrt(1f + rise * rise); // sine of the downhill angle
            slideSpeed += (-Physics.gravity.y * slideGravity * downhill - slideFriction) * dt;
        }
        slideSpeed = Mathf.Clamp(slideSpeed, 0f, slideMaxSpeed);

        if (wishDirection.sqrMagnitude > 0.01f)
            slideDirection = Vector3.RotateTowards(slideDirection, wishDirection.normalized, slideSteer * Mathf.Deg2Rad * dt, 0f);

        motor.HorizontalVelocity = slideDirection * slideSpeed;

        bool released = !crouchHeld && !slideHeldWithoutCrouch && slideTime > slideMinTime;
        if (slideSpeed < slideEndSpeed || released)
        {
            EndSlide();
            // Still holding crouch: end up crouched, already slowed to crouch speed.
            if (crouchHeld) crouch = Mathf.Max(crouch, 0.6f);
        }
    }

    // ----------------------------------------------------------------------------------
    // Dodge
    // ----------------------------------------------------------------------------------

    // A quick dash the way the player is steering (straight back when not steering). The body
    // keeps facing the same way, so it is a sidestep or a hop back in first person.
    void TryStartDodge(bool grounded)
    {
        bool canDodge = (grounded || Time.time - lastGroundedTime <= coyoteTime) &&
                        Time.time - lastDodgeEndTime > dodgeCooldown && crouch < 0.5f;
        if (!canDodge)
            return;

        Vector3 dir = wishDirection.sqrMagnitude > 0.01f
            ? wishDirection.normalized
            : Quaternion.Euler(0f, facingYaw, 0f) * Vector3.back;
        if (isSliding) EndSlide();
        isDodging = true;
        dodgeDirection = dir;
        dodgeTime = 0f;
        // Come out of it at the speed the player is asking for, so a dodge flows into a run.
        dodgeExitSpeed = wishDirection.sqrMagnitude > 0.01f ? (sprintHeld ? sprintSpeed : walkSpeed) * Mathf.Clamp01(wishDirection.magnitude) : 0f;
        sprintMomentum *= 0.5f;
    }

    // Fast at the start, easing to the exit speed. The peak is set so the dash covers dodgeDistance.
    void UpdateDodge(float dt)
    {
        dodgeTime += dt;
        float duration = Mathf.Max(dodgeDuration, 0.05f);
        float t = Mathf.Clamp01(dodgeTime / duration);
        // Speed curve exit + (peak - exit) * (1 - t)^2 averages exit + (peak - exit) / 3.
        float peak = Mathf.Max(dodgeExitSpeed, 3f * (dodgeDistance / duration) - 2f * dodgeExitSpeed);
        float speed = dodgeExitSpeed + (peak - dodgeExitSpeed) * (1f - t) * (1f - t);
        motor.HorizontalVelocity = dodgeDirection * speed;

        if (t >= 1f || motor.HitWall)
        {
            isDodging = false;
            lastDodgeEndTime = Time.time;
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

        if (isSliding)
        {
            idleTurning = false;
            target = Mathf.Atan2(slideDirection.x, slideDirection.z) * Mathf.Rad2Deg;
        }
        else if (isDodging)
        {
            idleTurning = false;
        }
        else if (motor.NoClip)
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
            Sliding = isSliding,
            Dodging = isDodging,
            DodgeDirection = dodgeDirection,
            DodgeProgress = isDodging ? Mathf.Clamp01(dodgeTime / Mathf.Max(dodgeDuration, 0.05f)) : 1f,
            Swimming = motor.IsSwimming,
            WaterDepth = motor.WaterDepth,
            Crouch = crouch,
            Slope = slope,
        };
    }
}
