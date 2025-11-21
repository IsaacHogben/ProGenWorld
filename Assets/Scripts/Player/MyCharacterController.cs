using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Voxel-focused character controller:
/// - Minecraft-style feel
/// - Half- and full-block stepping (via geometry)
/// - Slope sliding
/// - Drop tolerance
/// - Voxel grounding snap
/// 
/// Assumptions:
/// - World is built from 1x1x1-ish voxels (blockSize).
/// - CharacterController.center.y is always height / 2 (as you said).
/// - CharacterController rotation is identity (no tilt).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class VoxelCharacterController : MonoBehaviour
{
    [Header("Voxel World")]
    public float blockSize = 1f;        // size of 1 voxel cube
    public LayerMask groundMask = ~0;   // which layers are "terrain"

    [Header("Movement")]
    public float moveSpeed = 6f;
    public float gravity = -25f;
    public float jumpHeight = 1.2f;

    [Header("Look")]
    public float sensitivity = 2f;
    public Transform cameraPivot;       // pivot or camera parent

    [Header("Stepping / Grounding")]
    [Tooltip("Max vertical height we can step up in one go (typically 1 block).")]
    public float maxStepHeight = 1.0f;
    [Tooltip("Forward distance to look for a step.")]
    public float stepSearchDistance = 0.5f;
    [Tooltip("Max vertical gap to 'snap down' instead of free-fall.")]
    public float dropToleranceBlocks = 1.0f; // in blocks

    [Header("Slopes")]
    [Tooltip("Angle above which we start sliding down slopes.")]
    public float slideAngle = 50f;
    [Tooltip("Speed multiplier for sliding down steep slopes.")]
    public float slideSpeed = 6f;

    [Header("Voxel Snapping")]
    [Tooltip("Snap to nearest multiple of this when grounded (e.g. blockSize or blockSize * 0.5f).")]
    public float voxelSnapUnit = 0.5f; // half-block snap
    [Tooltip("Max distance from snap plane before we stop snapping.")]
    public float maxSnapOffset = 0.08f;

    // ---- Input buffer ----
    private Vector2 moveInput;
    private Vector2 lookInput;
    private bool jumpPressed;

    // ---- State ----
    private CharacterController cc;
    private Vector3 velocity;  // Y-component is vertical velocity; XZ from input
    private bool isGrounded;
    private float pitch;

    void Awake()
    {
        cc = GetComponent<CharacterController>();

        // If you change height, this automatically still works as long as:
        // cc.center.y == cc.height / 2f
    }

    // ----------------------------------------------------------
    // INPUT CALLBACKS (hook up via PlayerInput)
    // ----------------------------------------------------------
    public void OnMove(InputAction.CallbackContext ctx)
    {
        moveInput = ctx.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext ctx)
    {
        lookInput = ctx.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext ctx)
    {
        if (ctx.performed)
            jumpPressed = true;
    }

    // ----------------------------------------------------------
    // UPDATE
    // ----------------------------------------------------------
    void Update()
    {
        HandleLook();

        float dt = Time.deltaTime;

        // Ground check based on CC + small capsule cast
        isGrounded = CheckGrounded(out RaycastHit groundHit);

        // Vertical
        if (isGrounded && velocity.y < 0f)
            velocity.y = -2f; // small stick to ground

        if (isGrounded && jumpPressed)
        {
            jumpPressed = false;
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            isGrounded = false; // now in the air
        }

        // Apply gravity
        velocity.y += gravity * dt;

        // Horizontal input
        Vector3 inputDir = new Vector3(moveInput.x, 0f, moveInput.y);
        inputDir = transform.TransformDirection(inputDir);
        inputDir.Normalize();

        Vector3 horizontalVel = inputDir * moveSpeed;

        // Base motion from velocity
        Vector3 motion = new Vector3(horizontalVel.x, velocity.y, horizontalVel.z) * dt;

        // Step-up logic (voxel-aware)
        motion = ApplyVoxelStepUp(motion, inputDir);

        // Apply motion
        cc.Move(motion);

        // Re-check ground after move for slope + snap
        isGrounded = CheckGrounded(out groundHit);

        // If grounded after move, clamp vertical velocity
        if (isGrounded && velocity.y < 0f)
            velocity.y = 0f;

        // Apply slope sliding if standing on steep surface
        if (isGrounded && groundHit.collider != null)
            ApplySlopeSliding(ref groundHit, dt);

        // Small drop snapping (drop tolerance)
        if (!jumpPressed && velocity.y <= 0f)
            ApplyDropSnap(Time.deltaTime);

        // Final voxel snap (Y) when grounded to remove jitter
        if (isGrounded)
            SnapToVoxelGround();
    }

    // ----------------------------------------------------------
    // LOOK
    // ----------------------------------------------------------
    void HandleLook()
    {
        Vector2 delta = lookInput * sensitivity;

        transform.Rotate(Vector3.up * delta.x);

        pitch -= delta.y;
        pitch = Mathf.Clamp(pitch, -80f, 80f);

        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    // ----------------------------------------------------------
    // GROUND CHECK
    // ----------------------------------------------------------
    bool CheckGrounded(out RaycastHit hit)
    {
        // Use CC geometry to build capsule
        Vector3 centerWorld = transform.TransformPoint(cc.center);
        float halfHeight = cc.height * 0.5f;
        float bottomOffset = halfHeight - cc.radius;

        Vector3 bottom = centerWorld + Vector3.down * bottomOffset;
        Vector3 top = centerWorld + Vector3.up * bottomOffset;

        float groundCheckDistance = 0.2f;

        if (Physics.CapsuleCast(top, bottom, cc.radius, Vector3.down, out hit, groundCheckDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            return true;
        }

        return cc.isGrounded; // fallback
    }

    // ----------------------------------------------------------
    // VOXEL STEP-UP (Half + Full block via geometry)
    // ----------------------------------------------------------
    Vector3 ApplyVoxelStepUp(Vector3 motion, Vector3 moveDir)
    {
        // No input, no step
        if (moveDir.sqrMagnitude < 0.0001f)
            return motion;

        float dt = Time.deltaTime;

        // Get bottom of capsule in world space
        Vector3 centerWorld = transform.TransformPoint(cc.center);
        float halfHeight = cc.height * 0.5f;
        float bottomOffset = halfHeight - cc.radius;
        Vector3 bottom = centerWorld + Vector3.down * bottomOffset;

        // 1) Raycast forward from just above the feet to find a wall/block face
        Vector3 feetOrigin = bottom + Vector3.up * 0.05f; // tiny lift to avoid hitting floor
        Vector3 fwd = moveDir.normalized;

        if (!Physics.Raycast(
                feetOrigin,
                fwd,
                out RaycastHit wallHit,
                stepSearchDistance,
                groundMask,
                QueryTriggerInteraction.Ignore))
        {
            // Nothing directly in front at foot level
            return motion;
        }

        // 2) From slightly above max step height above current bottom,
        //    cast down to find the top surface of the step.
        float probeHeight = maxStepHeight + 0.2f; // a bit above max step
        Vector3 probeOrigin =
            new Vector3(wallHit.point.x, bottom.y + probeHeight, wallHit.point.z) +
            fwd * 0.05f; // nudge slightly over the block

        if (!Physics.Raycast(
                probeOrigin,
                Vector3.down,
                out RaycastHit stepHit,
                probeHeight + 0.5f,
                groundMask,
                QueryTriggerInteraction.Ignore))
        {
            // No top surface to stand on
            return motion;
        }

        // 3) Compute how far we need to move bottom up to stand on that surface
        float targetBottomY = stepHit.point.y + cc.radius;
        float currentBottomY = bottom.y;
        float stepDelta = targetBottomY - currentBottomY;

        // Only step UP, and only if within maxStepHeight
        if (stepDelta <= 0f || stepDelta > maxStepHeight + 0.05f)
            return motion;

        // 4) Perform the step:
        //    - Move up by the required stepDelta
        //    - Slightly forward to clear the vertical face
        cc.Move(new Vector3(0f, stepDelta, 0f));
        cc.Move(fwd * 0.05f);

        // 5) Rebuild this frame's motion after stepping
        Vector3 horizontalVel = moveDir * moveSpeed;
        Vector3 newMotion = new Vector3(horizontalVel.x, velocity.y, horizontalVel.z) * dt;

        return newMotion;
    }

    // ----------------------------------------------------------
    // SLOPE SLIDING (steep slopes only)
    // ----------------------------------------------------------
    void ApplySlopeSliding(ref RaycastHit groundHit, float dt)
    {
        float angle = Vector3.Angle(groundHit.normal, Vector3.up);
        if (angle < slideAngle)
            return;

        // Slide direction is gravity projected onto tangent plane
        Vector3 slideDir = Vector3.ProjectOnPlane(Vector3.down, groundHit.normal).normalized;
        Vector3 slideMotion = slideDir * slideSpeed * dt;

        cc.Move(slideMotion);
    }

    // ----------------------------------------------------------
    // DROP SNAP (small gaps only)
    // ----------------------------------------------------------
    Vector3 ApplyDropSnap(float dt)
    {
        // 1. Only snap when falling slowly, near-ground.
        if (velocity.y > -5f)   // don't snap if falling fast (jump or big drop)
            return Vector3.zero;

        // 2. Only snap if the controller is NOT grounded.
        if (cc.isGrounded)
            return Vector3.zero;

        float maxSnap = blockSize * dropToleranceBlocks; // usually 1 block
        float radius = cc.radius;
        float height = cc.height;
        float skin = cc.skinWidth;

        // Build capsule bottom/top
        Vector3 centerWorld = transform.TransformPoint(cc.center);
        float halfHeight = cc.height * 0.5f;
        float bottomOffset = halfHeight - cc.radius;

        Vector3 bottom = centerWorld + Vector3.down * bottomOffset;
        Vector3 top = centerWorld + Vector3.up * bottomOffset;

        // 3. Cast down to see if the ground is *very* close
        if (!Physics.CapsuleCast(top, bottom, radius, Vector3.down,
            out RaycastHit hit, maxSnap, groundMask, QueryTriggerInteraction.Ignore))
            return Vector3.zero;

        float dist = hit.distance;

        // 4. Too close: don't bother to snap
        if (dist < 0.02f)
            return Vector3.zero;

        // 5. Too far: treat it as real falling
        if (dist > maxSnap)
            return Vector3.zero;

        // 6. Check ground slope; don't snap to steep slopes
        if (Vector3.Angle(hit.normal, Vector3.up) > slideAngle)
            return Vector3.zero;

        // 7. Check vertical speed: DO NOT snap if we're significantly airborne
        if (velocity.y < -12f)  // tweak: the more negative, the more "in the air" we are
            return Vector3.zero;

        // 8. Snap down gently
        return Vector3.down * dist;
    }


    // ----------------------------------------------------------
    // VOXEL GROUND SNAP (no jitter)
    // ----------------------------------------------------------
    void SnapToVoxelGround()
    {
        // Snap so that the capsule BOTTOM aligns to nearest voxelSnapUnit multiple.

        Vector3 centerWorld = transform.TransformPoint(cc.center);
        float halfHeight = cc.height * 0.5f;
        float bottomOffset = halfHeight - cc.radius;
        Vector3 bottom = centerWorld + Vector3.down * bottomOffset;

        float snapUnit = Mathf.Max(voxelSnapUnit, 0.0001f);

        // Target "grid" height for bottom
        float snappedBottomY = Mathf.Round(bottom.y / snapUnit) * snapUnit;
        float deltaY = snappedBottomY - bottom.y;

        if (Mathf.Abs(deltaY) > maxSnapOffset)
            return;
    }
}
