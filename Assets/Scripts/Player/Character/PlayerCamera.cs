using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// First and third person camera for the player (V toggles, scroll zooms in third person, Esc
/// frees the cursor). Runs after animation so it sees the final head position.
/// </summary>
[DefaultExecutionOrder(100)]
public class PlayerCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Head bone used for the first person eye position (found by name if empty)")]
    [SerializeField] private Transform head;
    [SerializeField] private PlayerInput playerInput;

    [Header("Look")]
    [Tooltip("Degrees per mouse pixel")]
    [SerializeField] private float sensitivity = 0.15f;
    [SerializeField] private float minPitch = -80f;
    [SerializeField] private float maxPitch = 80f;

    [Header("First Person")]
    [Tooltip("Eye position relative to the head bone, in the body's facing frame")]
    [SerializeField] private Vector3 eyeOffset = new Vector3(0f, 0.15f, 0.2f);
    [Tooltip("Hide the head's renderers (shadows still cast) in first person")]
    [SerializeField] private bool hideHeadInFirstPerson = true;

    [Header("Third Person")]
    [SerializeField] private bool startInThirdPerson = true;
    [SerializeField] private Key toggleViewKey = Key.V;
    [Tooltip("Orbit point above the feet")]
    [SerializeField] private float pivotHeight = 2.5f;
    [Tooltip("Sideways offset of the orbit point (over the shoulder)")]
    [SerializeField] private float shoulderOffset = 0.6f;
    [SerializeField] private float distance = 6f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 14f;
    [Tooltip("Distance change per scroll notch")]
    [SerializeField] private float zoomStep = 0.8f;
    [Tooltip("Smoothing of the camera's vertical follow (seconds). Softens stairs and landings")]
    [SerializeField] private float verticalSmoothTime = 0.08f;
    [Tooltip("How far the camera stays from blocks")]
    [SerializeField] private float collisionPadding = 0.3f;
    [Tooltip("How fast the camera moves back out after being pushed in by terrain")]
    [SerializeField] private float collisionRecoverySpeed = 5f;

    private float yaw;
    private float pitch;
    private bool thirdPerson;
    private float currentDistance;
    private float targetDistance;
    private float smoothPivotY;
    private float pivotYVelocity;
    private InputAction lookAction;
    private PlayerController controller;
    private Renderer[] headRenderers;
    private bool headHidden;

    public float Yaw => yaw;
    public float Pitch => pitch;
    public bool IsThirdPerson => thirdPerson;
    public Vector3 LookDirection => Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

    void Awake()
    {
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        if (playerInput == null) playerInput = GetComponent<PlayerInput>();
        if (playerInput != null) lookAction = playerInput.actions.FindAction("Look");
        if (head == null) head = FindDeep(transform, "head");
        controller = GetComponent<PlayerController>();

        if (head != null)
            headRenderers = head.GetComponentsInChildren<Renderer>();

        yaw = transform.eulerAngles.y;
        thirdPerson = startInThirdPerson;
        targetDistance = currentDistance = distance;
        smoothPivotY = transform.position.y + pivotHeight;
    }

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleKeys();

        if (Cursor.lockState == CursorLockMode.Locked && lookAction != null)
        {
            Vector2 delta = lookAction.ReadValue<Vector2>();
            yaw += delta.x * sensitivity;
            pitch = Mathf.Clamp(pitch - delta.y * sensitivity, minPitch, maxPitch);
        }
    }

    void LateUpdate()
    {
        if (cameraTransform == null)
            return;

        SetHeadHidden(hideHeadInFirstPerson && !thirdPerson);

        if (thirdPerson) PlaceThirdPerson();
        else PlaceFirstPerson();
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
            {
                thirdPerson = !thirdPerson;
                smoothPivotY = transform.position.y + pivotHeight;
                pivotYVelocity = 0f;
            }
        }

        var mouse = Mouse.current;
        if (thirdPerson && mouse != null)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                targetDistance = Mathf.Clamp(targetDistance - Mathf.Sign(scroll) * zoomStep, minDistance, maxDistance);
        }
        distance = Mathf.Lerp(distance, targetDistance, 1f - Mathf.Exp(-12f * Time.deltaTime));
    }

    void PlaceFirstPerson()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 position = transform.position + Vector3.up * pivotHeight;
        if (head != null)
        {
            float facing = controller != null ? controller.FacingYaw : yaw;
            position = head.position + Quaternion.Euler(0f, facing, 0f) * eyeOffset;
        }
        cameraTransform.SetPositionAndRotation(position, rotation);
    }

    void PlaceThirdPerson()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);

        float pivotY = transform.position.y + pivotHeight;
        smoothPivotY = Mathf.SmoothDamp(smoothPivotY, pivotY, ref pivotYVelocity, verticalSmoothTime);
        Vector3 pivot = new Vector3(transform.position.x, smoothPivotY, transform.position.z);

        // Over the shoulder, pulled in if a wall is beside the player.
        Vector3 side = rotation * Vector3.right;
        pivot += side * CastClear(pivot, side, shoulderOffset);

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

    void SetHeadHidden(bool hidden)
    {
        if (hidden == headHidden || headRenderers == null)
            return;
        headHidden = hidden;
        foreach (var r in headRenderers)
            if (r != null)
                r.shadowCastingMode = hidden ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            var found = FindDeep(child, name);
            if (found != null) return found;
        }
        return null;
    }
}
