using UnityEngine;

/// <summary>
/// Handles physics collision and movement. Snaps up instantly on steps.
/// The visual body (parent) follows smoothly.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PhysicsBody : MonoBehaviour
{
    [Header("Step Detection")]
    [SerializeField] private float stepHeight = 1f;
    [SerializeField] private float stepCheckDistance = 0.5f;
    [SerializeField] private LayerMask terrainLayer;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.2f;

    private Rigidbody rb;
    private Vector3 moveVelocity;
    private bool isGrounded;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Important physics settings
        rb.useGravity = true;
        rb.mass = 1f;
        rb.linearDamping = 0f; // No drag
        rb.angularDamping = 0.05f;
    }

    void FixedUpdate()
    {
        CheckGround();
        CheckAndSnapStep();
    }

    void CheckGround()
    {
        // Simple ground check
        isGrounded = Physics.Raycast(transform.position, Vector3.down, groundCheckDistance, terrainLayer);
        Debug.DrawRay(transform.position, Vector3.down * groundCheckDistance, isGrounded ? Color.green : Color.red);
    }

    void CheckAndSnapStep()
    {
        if (!isGrounded || moveVelocity.magnitude < 0.1f) return;

        // Check for obstacle ahead at foot level
        Vector3 checkOrigin = transform.position + Vector3.up * 0.1f;
        Vector3 checkDirection = new Vector3(moveVelocity.x, 0, moveVelocity.z).normalized;

        if (Physics.Raycast(checkOrigin, checkDirection, stepCheckDistance, terrainLayer))
        {
            // Check if there's a walkable surface above
            Vector3 stepCheckOrigin = checkOrigin + checkDirection * stepCheckDistance + Vector3.up * stepHeight;
            RaycastHit hit;

            if (Physics.Raycast(stepCheckOrigin, Vector3.down, out hit, stepHeight * 1.5f, terrainLayer))
            {
                float stepUpAmount = hit.point.y - transform.position.y;

                if (stepUpAmount > 0.1f && stepUpAmount <= stepHeight)
                {
                    // SNAP up instantly and reset vertical velocity
                    Vector3 newPos = transform.position;
                    newPos.y = hit.point.y + 0.05f;
                    transform.position = newPos;

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