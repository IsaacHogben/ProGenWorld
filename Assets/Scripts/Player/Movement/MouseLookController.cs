using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simplified mouse look with separated camera and body rotation
/// Camera is independent, body follows when needed
/// </summary>
public class MouseLookController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraTransform; // Independent camera (child of Player root)
    [SerializeField] private Transform bodyTransform; // Body that rotates (child of Player root)
    [SerializeField] private Transform headTransform; // Head (child of Body)

    [Header("Mouse Sensitivity")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float verticalSensitivity = 2f;

    [Header("Camera Limits")]
    [SerializeField] private float maxPitch = 80f; // How far camera can look up
    [SerializeField] private float minPitch = -80f; // How far camera can look down

    [Header("Body Rotation")]
    [SerializeField] private float maxBodyAngleDifference = 80f; // Max angle between body and camera before body follows
    [SerializeField] private float bodyRotationSpeed = 10f; // How fast body rotates
    [SerializeField] private bool rotateBodyOnMovement = true; // Body faces camera when walking

    // Camera height tracking
    private Vector3 cameraToHeadOffset = Vector3.zero; // Calculated offset from head to camera

    // Rotation values
    private float cameraYaw = 0f; // Camera horizontal rotation
    private float cameraPitch = 0f; // Camera vertical rotation

    // Input
    private Vector2 lookInput;

    // Reference to movement controller
    private SimpleVoxelController simpleController;

    void Start()
    {
        // Lock and hide cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Calculate camera offset from head initial positions
        if (cameraTransform != null && headTransform != null)
        {
            cameraToHeadOffset = cameraTransform.position - headTransform.position;
        }

        // Initialize camera rotation from current body rotation
        if (bodyTransform != null)
        {
            cameraYaw = bodyTransform.eulerAngles.y;
        }
    }

    void Update()
    {
        HandleCameraRotation();
        HandleBodyRotation();
        HandleCameraPosition();
        HandleHeadRotation();
    }

    void HandleCameraRotation()
    {
        if (cameraTransform == null) return;

        // Update camera rotation based on mouse input (invert pitch)
        cameraYaw += lookInput.x * mouseSensitivity;
        cameraPitch -= lookInput.y * verticalSensitivity; // Changed back to -= for correct up/down

        // Clamp vertical look
        cameraPitch = Mathf.Clamp(cameraPitch, minPitch, maxPitch);

        // Apply rotation to camera
        cameraTransform.rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
    }

    void HandleCameraPosition()
    {
        if (cameraTransform == null || headTransform == null) return;

        // Position camera relative to head using the calculated offset
        cameraTransform.position = headTransform.position + cameraToHeadOffset;
    }

    void HandleBodyRotation()
    {
        if (bodyTransform == null) return;

        float currentBodyYaw = bodyTransform.eulerAngles.y;

        // Only rotate body when head has turned too far
        float angleDifference = Mathf.DeltaAngle(currentBodyYaw, cameraYaw + 180);

        if (Mathf.Abs(angleDifference) > maxBodyAngleDifference)
        {
            float targetBodyYaw = cameraYaw + 180; // Add 180 to flip body direction
            float newBodyYaw = Mathf.LerpAngle(currentBodyYaw, targetBodyYaw, bodyRotationSpeed * Time.deltaTime);

            // Preserve the X (tilt) rotation that's being set by ProceduralLegIK
            Vector3 currentEuler = bodyTransform.eulerAngles;
            bodyTransform.rotation = Quaternion.Euler(currentEuler.x, newBodyYaw, 0);
        }
    }

    void HandleHeadRotation()
    {
        if (headTransform == null || bodyTransform == null) return;

        // Calculate angle difference between camera and body
        float bodyYaw = bodyTransform.eulerAngles.y;
        float headYawTarget = Mathf.DeltaAngle(bodyYaw, cameraYaw + 180); // Add 180 for model orientation

        // Rotate head to face camera direction (relative to body)
        // Negate pitch to invert up/down
        headTransform.localRotation = Quaternion.Euler(-cameraPitch, headYawTarget, 0);
    }

    // Input System callback
    public void OnLook(InputAction.CallbackContext context)
    {
        lookInput = context.ReadValue<Vector2>();
    }

    // Allow cursor toggle with Escape
    void LateUpdate()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    // Public accessors
    public float CameraYaw => cameraYaw;
    public float CameraPitch => cameraPitch;
    public float BodyYaw => bodyTransform != null ? bodyTransform.eulerAngles.y : 0;
}