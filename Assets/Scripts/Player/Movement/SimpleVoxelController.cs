using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Voxel character controller: camera relative walk/sprint/crouch, jumping with coyote time and
/// input buffering. Collision, step-up and ground snapping are done by <see cref="VoxelBody"/>;
/// this script only decides the velocity and which way the body faces.
/// </summary>
[RequireComponent(typeof(VoxelBody))]
public class SimpleVoxelController : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Normal walking speed")]
    [SerializeField] private float walkSpeed = 5f;
    [Tooltip("Sprint speed (hold Shift)")]
    [SerializeField] private float sprintSpeed = 8f;
    [Tooltip("Crouch speed")]
    [SerializeField] private float crouchSpeed = 2.5f;
    [Tooltip("How quickly to accelerate/decelerate (1/s)")]
    [SerializeField] private float acceleration = 20f;
    [Tooltip("Movement control while airborne (0 = no control, 1 = full control)")]
    [SerializeField] private float airControlMultiplier = 0.3f;
    [Tooltip("Maximum falling speed")]
    [SerializeField] private float terminalVelocity = -20f;

    [Header("Crouch")]
    [Tooltip("How much to lower body when crouching")]
    [SerializeField] private float crouchHeight = 0.5f;
    [Tooltip("Speed of crouch transition")]
    [SerializeField] private float crouchTransitionSpeed = 8f;

    [Header("Stepping")]
    [Tooltip("Maximum ledge height climbed automatically (1 = one block)")]
    [SerializeField] private float maxStepHeight = 1f;
    [Tooltip("Walking off a drop up to this high keeps you on the ground instead of falling (0 = off)")]
    [SerializeField] private float groundSnapDistance = 1f;

    [Header("Jump")]
    [Tooltip("Upward velocity applied when jumping")]
    [SerializeField] private float jumpForce = 7f;
    [Tooltip("Time after leaving ground where jump is still allowed (coyote time)")]
    [SerializeField] private float coyoteTime = 0.1f;
    [Tooltip("A jump pressed this long before landing still happens on landing")]
    [SerializeField] private float jumpBufferTime = 0.15f;

    [Header("References")]
    [Tooltip("Camera for relative movement (optional, uses MouseLookController when present)")]
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Body transform that rotates to face movement direction")]
    [SerializeField] private Transform bodyTransform;

    [Header("Body Rotation")]
    [Tooltip("Rotate the body to face the way it is going")]
    [SerializeField] private bool rotateBodyToMovement = true;
    [Tooltip("Speed of body rotation")]
    [SerializeField] private float bodyRotationSpeed = 10f;
    [Tooltip("When standing still, the body turns to follow the camera once the look angle passes this")]
    [SerializeField] private float idleTurnAngle = 65f;

    // Movement states for IK
    public enum MovementState { Idle, Turning, Walking, Sprinting, Crouching, Jumping, Falling }

    // The model faces away from the body transform's forward axis.
    const float ModelYawOffset = 180f;

    // Components
    private VoxelBody body;
    private MouseLookController look;

    // Input
    private Vector2 moveInputRaw;
    private bool sprintHeld;
    private bool isCrouching;
    private float jumpPressedTime = float.NegativeInfinity;

    // State
    private Vector3 moveInput;
    private Vector3 currentVelocity;
    private bool isGrounded;
    private bool isJumping;
    private float lastGroundedTime = float.NegativeInfinity;
    private float currentCrouchOffset;
    private float bodyYaw;
    private bool idleTurning;
    private MovementState currentState = MovementState.Idle;

    void Awake()
    {
        // Terrain collision reads the voxel data directly (VoxelPhysics) instead of Unity physics.
        if (!TryGetComponent(out body))
            body = gameObject.AddComponent<VoxelBody>();
        body.useGravity = true;
        body.interpolate = true;

        look = GetComponent<MouseLookController>();

        if (cameraTransform == null)
            cameraTransform = Camera.main != null ? Camera.main.transform : null;

        if (bodyTransform != null)
            bodyYaw = bodyTransform.eulerAngles.y;
    }

    // Runs after VoxelBody.FixedUpdate: reads the result of this step, sets the velocity for the next.
    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        CalculateMovementInput();

        isGrounded = body.IsGrounded;
        if (isGrounded)
        {
            lastGroundedTime = Time.time;
            if (isJumping && body.linearVelocity.y <= 0f)
                isJumping = false;
        }

        // Horizontal velocity eases toward the target (frame-rate independent).
        float accel = isGrounded ? acceleration : acceleration * airControlMultiplier;
        Vector3 targetVelocity = moveInput * CurrentMaxSpeed();
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, 1f - Mathf.Exp(-accel * dt));

        Vector3 vel = body.linearVelocity;
        vel.x = currentVelocity.x;
        vel.z = currentVelocity.z;

        bool jumpBuffered = Time.time - jumpPressedTime <= jumpBufferTime;
        bool canJump = !isJumping && !isCrouching && (isGrounded || Time.time - lastGroundedTime <= coyoteTime);
        if (jumpBuffered && canJump)
        {
            vel.y = jumpForce;
            isJumping = true;
            isGrounded = false;
            jumpPressedTime = float.NegativeInfinity;
            lastGroundedTime = float.NegativeInfinity;
        }

        if (vel.y < terminalVelocity)
            vel.y = terminalVelocity;

        body.linearVelocity = vel;

        // Small tolerance so whole blocks are always reachable despite the collision skin.
        body.stepHeight = maxStepHeight > 0f ? maxStepHeight + 0.02f : 0f;
        body.groundSnapDistance = !isJumping && groundSnapDistance > 0f ? groundSnapDistance + 0.02f : 0f;

        float targetCrouch = isCrouching ? -crouchHeight : 0f;
        currentCrouchOffset = Mathf.Lerp(currentCrouchOffset, targetCrouch, 1f - Mathf.Exp(-crouchTransitionSpeed * dt));

        UpdateMovementState();
    }

    // Body facing is visual only, so it runs per frame for smoothness.
    void Update()
    {
        if (bodyTransform == null)
            return;

        float cameraYaw = CameraYaw() + ModelYawOffset;
        bool moving = moveInput.sqrMagnitude > 0.01f;
        float targetYaw = bodyYaw;

        if (moving && rotateBodyToMovement)
        {
            idleTurning = false;
            bool faceMovement = look != null && look.IsThirdPerson;
            targetYaw = faceMovement
                ? Mathf.Atan2(moveInput.x, moveInput.z) * Mathf.Rad2Deg + ModelYawOffset
                : cameraYaw;
        }
        else if (!moving && (look == null || !look.IsThirdPerson))
        {
            // Turn in place when the head has turned too far.
            float diff = Mathf.Abs(Mathf.DeltaAngle(bodyYaw, cameraYaw));
            if (diff > idleTurnAngle) idleTurning = true;
            else if (diff < 3f) idleTurning = false;
            if (idleTurning) targetYaw = cameraYaw;
        }
        else
        {
            idleTurning = false;
        }

        float speed = idleTurning ? bodyRotationSpeed * 0.8f : bodyRotationSpeed;
        bodyYaw = Mathf.LerpAngle(bodyYaw, targetYaw, 1f - Mathf.Exp(-speed * Time.deltaTime));

        // ProceduralLegIK adds lean on top of this in LateUpdate.
        bodyTransform.rotation = Quaternion.Euler(0f, bodyYaw, 0f);
    }

    float CameraYaw()
    {
        if (look != null)
            return look.CameraYaw;
        return cameraTransform != null ? cameraTransform.eulerAngles.y : transform.eulerAngles.y;
    }

    void CalculateMovementInput()
    {
        Quaternion yaw = Quaternion.Euler(0f, CameraYaw(), 0f);
        Vector3 forward = yaw * Vector3.forward;
        Vector3 right = yaw * Vector3.right;

        Vector3 wish = forward * moveInputRaw.y + right * moveInputRaw.x;
        moveInput = wish.sqrMagnitude > 1f ? wish.normalized : wish;
    }

    bool SprintActive => sprintHeld && !isCrouching && moveInputRaw.y > 0.1f;

    float CurrentMaxSpeed()
    {
        if (isCrouching) return crouchSpeed;
        return SprintActive ? sprintSpeed : walkSpeed;
    }

    void UpdateMovementState()
    {
        if (isJumping)
            currentState = MovementState.Jumping;
        else if (!isGrounded)
            currentState = MovementState.Falling;
        else if (isCrouching)
            currentState = MovementState.Crouching;
        else if (moveInput.sqrMagnitude < 0.01f)
            currentState = idleTurning ? MovementState.Turning : MovementState.Idle;
        else
            currentState = SprintActive ? MovementState.Sprinting : MovementState.Walking;
    }

    // Input callbacks (wired up by PlayerInput)
    public void OnMove(InputAction.CallbackContext context)
    {
        moveInputRaw = context.ReadValue<Vector2>();
    }

    public void OnSprint(InputAction.CallbackContext context)
    {
        sprintHeld = context.ReadValueAsButton();
    }

    public void OnCrouch(InputAction.CallbackContext context)
    {
        isCrouching = context.ReadValueAsButton();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
            jumpPressedTime = Time.time;
    }

    // Public accessors
    public Vector3 MoveDirection => moveInput;
    public float CurrentSpeed => currentVelocity.magnitude;
    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public bool IsGrounded => isGrounded;
    public MovementState State => currentState;
    public bool IsSprinting => SprintActive;
    public bool IsFalling => currentState == MovementState.Falling;
    public bool IsJumping => isJumping;
    public bool IsCrouching => isCrouching;
    public float CrouchOffset => currentCrouchOffset;
    public float BodyYaw => bodyYaw;
    public VoxelBody Body => body;
}
