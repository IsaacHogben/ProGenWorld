using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Mouse look with first and third person cameras (toggle with V, scroll to zoom in third person).
/// The camera is independent of the body; SimpleVoxelController turns the body to follow it.
/// Runs late so the camera sees the final animated head and the smoothed body position.
/// </summary>
[DefaultExecutionOrder(100)]
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

    [Header("Head")]
    [Tooltip("Max head yaw relative to the body")]
    [SerializeField] private float maxHeadYaw = 75f;

    [Header("Third Person")]
    [SerializeField] private bool startInThirdPerson = true;
    [SerializeField] private Key toggleViewKey = Key.V;
    [Tooltip("Orbit point above the feet")]
    [SerializeField] private float pivotHeight = 2.4f;
    [Tooltip("Sideways offset of the orbit point (over the shoulder)")]
    [SerializeField] private float shoulderOffset = 0.6f;
    [SerializeField] private float distance = 5f;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float maxDistance = 12f;
    [SerializeField] private float zoomSpeed = 0.01f;
    [Tooltip("How far the camera stays from blocks")]
    [SerializeField] private float collisionPadding = 0.3f;
    [Tooltip("How fast the camera moves back out after being pushed in by terrain")]
    [SerializeField] private float collisionRecoverySpeed = 6f;

    // Camera offset from head in first person
    private Vector3 cameraToHeadOffset = Vector3.zero;

    // Rotation values
    private float cameraYaw = 0f; // Camera horizontal rotation
    private float cameraPitch = 0f; // Camera vertical rotation

    private bool thirdPerson;
    private float currentDistance;

    // Input
    private Vector2 lookInput;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (cameraTransform != null && headTransform != null)
            cameraToHeadOffset = cameraTransform.position - headTransform.position;

        // Body model faces away from its forward axis.
        if (bodyTransform != null)
            cameraYaw = bodyTransform.eulerAngles.y + 180f;

        thirdPerson = startInThirdPerson;
        currentDistance = distance;
    }

    void Update()
    {
        HandleKeys();
        HandleCameraRotation();
    }

    void LateUpdate()
    {
        HandleHeadRotation();
        if (thirdPerson) PlaceThirdPersonCamera();
        else PlaceFirstPersonCamera();
    }

    void HandleKeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                bool locked = Cursor.lockState == CursorLockMode.Locked;
                Cursor.lockState = locked ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = locked;
            }

            if (keyboard[toggleViewKey].wasPressedThisFrame)
                thirdPerson = !thirdPerson;
        }

        var mouse = Mouse.current;
        if (thirdPerson && mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                distance = Mathf.Clamp(distance - scroll * zoomSpeed, minDistance, maxDistance);
        }
    }

    void HandleCameraRotation()
    {
        if (Cursor.lockState != CursorLockMode.Locked)
            return;

        cameraYaw += lookInput.x * mouseSensitivity;
        cameraPitch -= lookInput.y * verticalSensitivity;
        cameraPitch = Mathf.Clamp(cameraPitch, minPitch, maxPitch);
    }

    void PlaceFirstPersonCamera()
    {
        if (cameraTransform == null) return;

        cameraTransform.rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
        if (headTransform != null)
            cameraTransform.position = headTransform.position + cameraToHeadOffset;
    }

    void PlaceThirdPersonCamera()
    {
        if (cameraTransform == null) return;

        Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
        Vector3 pivot = transform.position + Vector3.up * pivotHeight;

        // Shoulder offset, pulled in if a wall is beside the player.
        Vector3 side = rotation * Vector3.right;
        float shoulder = CastClear(pivot, side, shoulderOffset);
        pivot += side * shoulder;

        Vector3 back = rotation * Vector3.back;
        float clear = CastClear(pivot, back, distance);

        // Snap in when blocked, ease back out once clear.
        if (clear < currentDistance)
            currentDistance = clear;
        else
            currentDistance = Mathf.Lerp(currentDistance, clear, 1f - Mathf.Exp(-collisionRecoverySpeed * Time.deltaTime));

        cameraTransform.SetPositionAndRotation(pivot + back * currentDistance, rotation);
    }

    // Distance along dir that is free of blocks, keeping collisionPadding away from them.
    float CastClear(Vector3 origin, Vector3 dir, float maxDist)
    {
        if (VoxelPhysics.Raycast(origin, dir, out VoxelRaycastHit hit, maxDist + collisionPadding))
            return Mathf.Max(0f, hit.distance - collisionPadding);
        return maxDist;
    }

    void HandleHeadRotation()
    {
        if (headTransform == null || bodyTransform == null) return;

        // Look direction relative to the body (+180 for model orientation).
        float bodyYaw = bodyTransform.eulerAngles.y;
        float headYaw = Mathf.Clamp(Mathf.DeltaAngle(bodyYaw, cameraYaw + 180), -maxHeadYaw, maxHeadYaw);

        // Negate pitch to invert up/down for the model.
        headTransform.localRotation = Quaternion.Euler(-cameraPitch, headYaw, 0);
    }

    // Input System callback
    public void OnLook(InputAction.CallbackContext context)
    {
        lookInput = context.ReadValue<Vector2>();
    }

    // Public accessors
    public float CameraYaw => cameraYaw;
    public float CameraPitch => cameraPitch;
    public bool IsThirdPerson => thirdPerson;
    public float BodyYaw => bodyTransform != null ? bodyTransform.eulerAngles.y : 0;
}
