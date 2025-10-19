using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class SpectatorController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 8f;
    public float sprintMultiplier = 1.5f;
    public float lookSensitivity = 2f;
    public float verticalLookLimit = 85f;

    private SpectatorControls controls;
    private CharacterController controller;
    private Vector2 moveInput;
    private Vector2 lookInput;
    private float upDownInput;
    private float cameraPitch;
    private Transform cam;

    void Awake()
    {
        controls = new SpectatorControls();
        controller = GetComponent<CharacterController>();
        cam = Camera.main.transform;

        controls.Player.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        controls.Player.Move.canceled += _ => moveInput = Vector2.zero;

        controls.Player.Look.performed += ctx => lookInput = ctx.ReadValue<Vector2>();
        controls.Player.Look.canceled += _ => lookInput = Vector2.zero;

        controls.Player.UpDown.performed += ctx => upDownInput = ctx.ReadValue<float>();
        controls.Player.UpDown.canceled += _ => upDownInput = 0f;
    }

    void OnEnable() => controls.Player.Enable();
    void OnDisable() => controls.Player.Disable();

    void Update()
    {
        HandleLook();
        HandleMove();
    }

    void HandleLook()
    {
        float mouseX = lookInput.x * lookSensitivity * Time.deltaTime * 100f;
        float mouseY = lookInput.y * lookSensitivity * Time.deltaTime * 100f;

        cameraPitch -= mouseY;
        cameraPitch = Mathf.Clamp(cameraPitch, -verticalLookLimit, verticalLookLimit);

        cam.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    void HandleMove()
    {
        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y + Vector3.up * upDownInput;
        bool sprinting = controls.Player.Sprint.IsPressed();
        float speed = moveSpeed * (sprinting ? sprintMultiplier : 1f);

        controller.Move(move * speed * Time.deltaTime);
    }
}
