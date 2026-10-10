using UnityEngine;

/// <summary>
/// Handles physics collision and movement. Snaps up instantly on steps.
/// The visual body (parent) follows smoothly.
/// </summary>
[RequireComponent(typeof(VoxelBody))]
public class PhysicsBody : MonoBehaviour
{
    [Header("Step Detection")]
    [SerializeField] private float stepHeight = 1f;
    [SerializeField] private float stepCheckDistance = 0.5f;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.2f;

    private VoxelBody rb;
    private Vector3 moveVelocity;
    private bool isGrounded;

    void Start()
    {
        if (!TryGetComponent(out rb))
            rb = gameObject.AddComponent<VoxelBody>();
        rb.useGravity = true;
    }

    void FixedUpdate()
    {
        CheckGround();
        CheckAndSnapStep();
    }

    void CheckGround()
    {
        // Simple ground check
        isGrounded = VoxelPhysics.Raycast(transform.position + Vector3.up * 0.05f, Vector3.down, groundCheckDistance + 0.05f);
        Debug.DrawRay(transform.position, Vector3.down * groundCheckDistance, isGrounded ? Color.green : Color.red);
    }

    void CheckAndSnapStep()
    {
        if (!isGrounded || moveVelocity.magnitude < 0.1f) return;

        // Check for obstacle ahead at foot level
        Vector3 checkOrigin = transform.position + Vector3.up * 0.1f;
        Vector3 checkDirection = new Vector3(moveVelocity.x, 0, moveVelocity.z).normalized;

        if (VoxelPhysics.Raycast(checkOrigin, checkDirection, stepCheckDistance))
        {
            // Check if there's a walkable surface above
            Vector3 stepCheckOrigin = checkOrigin + checkDirection * stepCheckDistance + Vector3.up * stepHeight;
            if (VoxelPhysics.Raycast(stepCheckOrigin, Vector3.down, out VoxelRaycastHit hit, stepHeight * 1.5f))
            {
                float stepUpAmount = hit.point.y - transform.position.y;

                if (stepUpAmount > 0.1f && stepUpAmount <= stepHeight)
                {
                    // SNAP up instantly and reset vertical velocity
                    Vector3 newPos = rb.position;
                    newPos.y = hit.point.y + 0.05f;
                    rb.MovePosition(newPos);

                    // Reset vertical velocity to prevent launching
                    Vector3 vel = rb.linearVelocity;
                    vel.y = 0;
                    rb.linearVelocity = vel;

                    Debug.Log($"Snapped up by {stepUpAmount:F2}m");
                }
            }
        }
    }

    // Called by movement controller
    public void SetVelocity(Vector3 velocity)
    {
        moveVelocity = velocity;

        // Only set horizontal velocity, preserve vertical (gravity)
        Vector3 newVel = rb.linearVelocity;
        newVel.x = velocity.x;
        newVel.z = velocity.z;
        rb.linearVelocity = newVel;
    }

    public bool IsGrounded => isGrounded;
    public Vector3 Position => transform.position;
}