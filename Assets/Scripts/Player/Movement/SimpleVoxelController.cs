using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simple voxel character controller with smooth step up/down and sprint
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SimpleVoxelController : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Normal walking speed")]
    [SerializeField] private float walkSpeed = 5f;
    [Tooltip("Sprint speed (hold Shift)")]
    [SerializeField] private float sprintSpeed = 8f;
    [Tooltip("Crouch speed")]
    [SerializeField] private float crouchSpeed = 2.5f;
    [Tooltip("How quickly to accelerate/decelerate")]
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
    [Tooltip("Maximum height character can step up")]
    [SerializeField] private float maxStepHeight = 1f;
    [Tooltip("How far ahead to check for steps")]
    [SerializeField] private float stepCheckDistance = 0.5f;
    [Tooltip("Speed to move up when stepping (walking)")]
    [SerializeField] private float walkStepUpSpeed = 5f;
    [Tooltip("Speed to move up when stepping (sprinting)")]
    [SerializeField] private float sprintStepUpSpeed = 8f;

    [Header("Jump")]
    [Tooltip("Jump force")]
    [SerializeField] private float jumpForce = 7f;
    [Tooltip("Time after leaving ground where jump is still allowed (coyote time)")]
    [SerializeField] private float coyoteTime = 0.1f;

    [Header("Ground Detection")]
    [Tooltip("Distance to check for ground below")]
    [SerializeField] private float groundCheckDistance = 0.2f;
    [Tooltip("Additional distance to check for ground when jumping/falling (for early landing detection)")]
    [SerializeField] private float airborneGroundCheckDistance = 1.0f;
    [Tooltip("Terrain layer mask")]
    [SerializeField] private LayerMask terrainLayer;

    [Header("References")]
    [Tooltip("Camera for relative movement (optional)")]
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Body transform that rotates to face movement direction")]
    [SerializeField] private Transform bodyTransform;

    [Header("Body Rotation")]
    [Tooltip("Rotate body to face movement direction")]
    [SerializeField] private bool rotateBodyToMovement = true;
    [Tooltip("Speed of body rotation")]
    [SerializeField] private float bodyRotationSpeed = 10f;

    // Movement states for IK
    public enum MovementState { Idle, Turning, Walking, Sprinting, Crouching, Jumping, Falling }

    // Components
    private Rigidbody rb;

    // State
    private Vector2 moveInputRaw;
    private Vector3 moveInput;
    private Vector3 currentVelocity;
    private bool isGrounded;
    private float targetHeight;
    private bool isStepping;
    private bool isSprinting;
    private bool isCrouching;
    private float currentCrouchOffset = 0f;
    private bool jumpRequested;
    private bool isJumping;
    private float timeLeftGround;
    private MovementState currentState = MovementState.Idle;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.useGravity = true;
        rb.mass = 1f;
        rb.linearDamping = 0f;

        if (cameraTransform == null)
        {
            cameraTransform = Camera.main?.transform;
        }

        targetHeight = transform.position.y;
    }

    void FixedUpdate()
    {
        CalculateMovementInput();
        CheckGround();
        UpdateMovementState();
        HandleJump();
        HandleCrouch();
        CheckForSteps();
        ApplyMovement();
        RotateBody();
    }

    void CalculateMovementInput()
    {
        // Calculate camera-relative movement
        if (cameraTransform != null)
        {
            Vector3 forward = cameraTransform.forward;
            Vector3 right = cameraTransform.right;

            forward.y = 0;
            right.y = 0;
            forward.Normalize();
            right.Normalize();

            moveInput = (forward * moveInputRaw.y + right * moveInputRaw.x).normalized;
        }
        else
        {
            moveInput = new Vector3(moveInputRaw.x, 0, moveInputRaw.y).normalized;
        }
    }

    void CheckGround()
    {
        Vector3 rayStart = transform.position + Vector3.up * 0.1f;
        bool wasGrounded = isGrounded;

        // Only disable ground check while jumping AND moving upward
        if (isJumping && rb.linearVelocity.y > 0f)
        {
            isGrounded = false;
        }
        else
        {
            // Use extended distance when jumping/falling to detect landing earlier
            float checkDistance = isJumping ? (groundCheckDistance + airborneGroundCheckDistance) : (groundCheckDistance + 0.1f);
            isGrounded = Physics.Raycast(rayStart, Vector3.down, checkDistance, terrainLayer);
        }

        Debug.DrawRay(rayStart, Vector3.down * (groundCheckDistance + 0.1f), isGrounded ? Color.green : Color.red);

        // Track time since leaving ground for coyote time
        if (!isGrounded && wasGrounded)
        {
            timeLeftGround = Time.time;
        }

        // Clear jumping flag when we land
        if (isGrounded && isJumping)
        {
            isJumping = false;
        }
    }

    void UpdateMovementState()
    {
        // Check if jumping (highest priority)
        if (isJumping)
        {
            currentState = MovementState.Jumping;
            return;
        }

        // Check if falling (not grounded and not jumping)
        if (!isGrounded)
        {
            currentState = MovementState.Falling;
            return;
        }

        // Check if crouching
        if (isCrouching)
        {
            currentState = MovementState.Crouching;
            return;
        }

        if (moveInput.magnitude < 0.1f)
        {
            // Check if body is rotating (turning in place)
            if (bodyTransform != null && rotateBodyToMovement && cameraTransform != null)
            {
                Vector3 cameraForward = cameraTransform.forward;
                cameraForward.y = 0;
                cameraForward.Normalize();

                float targetYaw = Mathf.Atan2(cameraForward.x, cameraForward.z) * Mathf.Rad2Deg + 180f;
                float angleDiff = Mathf.Abs(Mathf.DeltaAngle(bodyTransform.eulerAngles.y, targetYaw));

                currentState = angleDiff > 5f ? MovementState.Turning : MovementState.Idle;
            }
            else
            {
                currentState = MovementState.Idle;
            }
        }
        else
        {
            // Sprint only works when moving forward (positive Y input) and not crouching
            bool isMovingForward = moveInputRaw.y > 0.1f;
            currentState = (isSprinting && isMovingForward) ? MovementState.Sprinting : MovementState.Walking;
        }
    }

    void HandleJump()
    {
        if (jumpRequested)
        {
            // Can't jump while crouching
            if (isCrouching)
            {
                jumpRequested = false;
                return;
            }

            // Allow jump if grounded OR within coyote time
            bool canJump = isGrounded || (Time.time - timeLeftGround < coyoteTime);

            if (canJump)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = jumpForce;
                rb.linearVelocity = vel;
                isGrounded = false;
                isJumping = true;
            }

            jumpRequested = false;
        }
    }

    void HandleCrouch()
    {
        // Calculate target crouch offset
        float targetCrouchOffset = isCrouching ? -crouchHeight : 0f;

        // Smoothly interpolate to target
        currentCrouchOffset = Mathf.Lerp(currentCrouchOffset, targetCrouchOffset, Time.fixedDeltaTime * crouchTransitionSpeed);
    }

    void CheckForSteps()
    {
        // Allow stepping when grounded OR when falling (not during upward jump)
        bool canStep = isGrounded || (rb.linearVelocity.y <= 0f);

        if (!canStep || moveInput.magnitude < 0.1f) return;

        Vector3 checkOrigin = transform.position + Vector3.up * 0.1f;
        Vector3 checkDirection = moveInput;

        RaycastHit obstacleHit;
        bool hasObstacle = Physics.Raycast(checkOrigin, checkDirection, out obstacleHit, stepCheckDistance, terrainLayer);

        Debug.DrawRay(checkOrigin, checkDirection * stepCheckDistance, hasObstacle ? Color.red : Color.blue);

        if (hasObstacle)
        {
            Vector3 stepCheckOrigin = checkOrigin + checkDirection * (obstacleHit.distance + 0.1f) + Vector3.up * (maxStepHeight + 0.1f);
            RaycastHit stepHit;

            if (Physics.Raycast(stepCheckOrigin, Vector3.down, out stepHit, maxStepHeight + 0.2f, terrainLayer))
            {
                float stepHeight = stepHit.point.y - transform.position.y;

                Debug.DrawRay(stepCheckOrigin, Vector3.down * (maxStepHeight + 0.2f), Color.cyan);

                if (stepHeight > 0.1f && stepHeight <= maxStepHeight)
                {
                    targetHeight = stepHit.point.y;
                    isStepping = true;
                }
            }
        }
    }

    void ApplyMovement()
    {
        // Determine movement speed based on state
        float moveSpeed = walkSpeed;
        if (isCrouching)
        {
            moveSpeed = crouchSpeed;
        }
        else
        {
            // Sprint only works when moving forward (positive Y input)
            bool isMovingForward = moveInputRaw.y > 0.1f;
            bool canSprint = isSprinting && isMovingForward;
            moveSpeed = canSprint ? sprintSpeed : walkSpeed;
        }

        float stepSpeed = isSprinting ? sprintStepUpSpeed : walkStepUpSpeed;

        // Reduce control when airborne (but maintain momentum)
        float accelMultiplier = isGrounded ? 1f : airControlMultiplier;

        // Horizontal movement
        if (moveInput.magnitude < 0.1f)
        {
            currentVelocity = Vector3.Lerp(currentVelocity, Vector3.zero, acceleration * accelMultiplier * Time.fixedDeltaTime);
        }
        else
        {
            Vector3 targetVelocity = moveInput * moveSpeed;
            currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, acceleration * accelMultiplier * Time.fixedDeltaTime);
        }

        // Vertical movement
        if (isStepping)
        {
            float heightDiff = targetHeight - transform.position.y;

            if (Mathf.Abs(heightDiff) < 0.05f)
            {
                Vector3 pos = transform.position;
                pos.y = targetHeight;
                transform.position = pos;
                isStepping = false;
                rb.linearVelocity = new Vector3(currentVelocity.x, 0, currentVelocity.z);
            }
            else
            {
                float newY = Mathf.MoveTowards(transform.position.y, targetHeight, stepSpeed * Time.fixedDeltaTime);
                Vector3 pos = transform.position;
                pos.y = newY;
                transform.position = pos;
                rb.linearVelocity = new Vector3(currentVelocity.x, 0, currentVelocity.z);
            }
        }
        else
        {
            Vector3 vel = rb.linearVelocity;
            vel.x = currentVelocity.x;
            vel.z = currentVelocity.z;

            // Apply terminal velocity
            if (vel.y < terminalVelocity)
            {
                vel.y = terminalVelocity;
            }

            rb.linearVelocity = vel;
        }
    }

    void RotateBody()
    {
        if (!rotateBodyToMovement || bodyTransform == null) return;

        if (moveInput.magnitude > 0.1f && cameraTransform != null)
        {
            Vector3 cameraForward = cameraTransform.forward;
            cameraForward.y = 0;
            cameraForward.Normalize();

            float targetYaw = Mathf.Atan2(cameraForward.x, cameraForward.z) * Mathf.Rad2Deg + 180f;
            float currentYaw = bodyTransform.eulerAngles.y;
            float newYaw = Mathf.LerpAngle(currentYaw, targetYaw, bodyRotationSpeed * Time.fixedDeltaTime);

            // Preserve the X rotation (tilt) that's being set by ProceduralLegIK
            Vector3 currentEuler = bodyTransform.eulerAngles;
            bodyTransform.rotation = Quaternion.Euler(currentEuler.x, newYaw, 0);
        }
    }

    // Input callbacks
    public void OnMove(InputAction.CallbackContext context)
    {
        moveInputRaw = context.ReadValue<Vector2>();
    }

    public void OnSprint(InputAction.CallbackContext context)
    {
        isSprinting = context.ReadValueAsButton();
    }

    public void OnCrouch(InputAction.CallbackContext context)
    {
        isCrouching = context.ReadValueAsButton();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            jumpRequested = true;
        }
    }

    // Public accessors
    public Vector3 MoveDirection => moveInput;
    public float CurrentSpeed => currentVelocity.magnitude;
    public bool IsGrounded => isGrounded;
    public MovementState State => currentState;
    public bool IsSprinting => isSprinting;
    public bool IsFalling => currentState == MovementState.Falling;
    public bool IsJumping => isJumping;
    public bool IsCrouching => isCrouching;
    public float CrouchOffset => currentCrouchOffset;
}