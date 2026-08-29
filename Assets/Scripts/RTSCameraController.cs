using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
// "Touch" exists in both the legacy UnityEngine input system and the new
// Input System's EnhancedTouch - the alias above forces the new one,
// resolving CS0104 (ambiguous reference).

/// <summary>
/// RTS-style camera: drag-to-pan and pinch-to-zoom on touch devices.
/// Also supports right-click-drag + mouse scroll in the Editor for testing
/// without a physical device.
///
/// Attach this to your Main Camera. Camera is expected to be angled
/// (e.g. rotated ~45-55 degrees on X) rather than a pure top-down ortho view,
/// matching the 0 A.D.-style look.
/// </summary>
[RequireComponent(typeof(Camera))]
public class RTSCameraController : MonoBehaviour
{
    [Header("Pan Settings")]
    [SerializeField] private float panSpeed = 1.2f;
    [SerializeField] private Vector2 panBoundsX = new Vector2(-60f, 60f);
    [SerializeField] private Vector2 panBoundsZ = new Vector2(-60f, 60f);

    [Header("Zoom Settings")]
    [SerializeField] private float zoomSpeed = 0.05f;
    [SerializeField] private float minHeight = 8f;
    [SerializeField] private float maxHeight = 45f;

    [Header("Editor Testing")]
    [SerializeField] private float editorScrollZoomSpeed = 4f;

    private Camera cam;
    private Vector2 lastPanScreenPos;
    private bool isPanning;

    private float lastPinchDistance;
    private bool isPinching;

    private Vector3 forwardFlat;
    private Vector3 rightFlat;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        CacheFlatAxes();
    }

    private void OnEnable()
    {
        // Required at runtime - without this, Touch.activeTouches throws
        // an InvalidOperationException even though it now compiles fine.
        EnhancedTouchSupport.Enable();
    }

    private void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    private void CacheFlatAxes()
    {
        // Camera is angled, so we flatten its forward/right onto the XZ plane
        // for panning, so "drag up" always means "move into the map" regardless
        // of camera tilt.
        Vector3 f = transform.forward; f.y = 0f; forwardFlat = f.normalized;
        Vector3 r = transform.right; r.y = 0f; rightFlat = r.normalized;
    }

    private void Update()
    {
        HandleTouch();

#if UNITY_EDITOR
        HandleEditorMouse();
#endif
    }

    private void HandleTouch()
    {
        var touches = Touch.activeTouches;

        if (touches.Count == 1)
        {
            isPinching = false;
            var t = touches[0];

            switch (t.phase)
            {
                case UnityEngine.InputSystem.TouchPhase.Began:
                    lastPanScreenPos = t.screenPosition;
                    isPanning = true;
                    break;
                case UnityEngine.InputSystem.TouchPhase.Moved:
                    if (isPanning)
                    {
                        Vector2 delta = t.screenPosition - lastPanScreenPos;
                        Pan(delta);
                        lastPanScreenPos = t.screenPosition;
                    }
                    break;
                case UnityEngine.InputSystem.TouchPhase.Ended:
                case UnityEngine.InputSystem.TouchPhase.Canceled:
                    isPanning = false;
                    break;
            }
        }
        else if (touches.Count == 2)
        {
            isPanning = false;
            float currentDistance = Vector2.Distance(touches[0].screenPosition, touches[1].screenPosition);

            if (!isPinching)
            {
                lastPinchDistance = currentDistance;
                isPinching = true;
            }
            else
            {
                float delta = currentDistance - lastPinchDistance;
                Zoom(-delta * zoomSpeed);
                lastPinchDistance = currentDistance;
            }
        }
        else
        {
            isPanning = false;
            isPinching = false;
        }
    }

#if UNITY_EDITOR
    private float debugLogTimer = 0f;
    private void HandleEditorMouse()
    {
        if (Mouse.current == null) return;

        // Temporary diagnostic: log raw button state every ~0.5s regardless
        // of press/release transitions, so we catch it even if something is
        // eating the transition events.
        debugLogTimer += Time.unscaledDeltaTime;
        if (debugLogTimer > 0.5f)
        {
            debugLogTimer = 0f;
            Debug.Log("[RTSCam] DIAG - rightButton.isPressed=" + Mouse.current.rightButton.isPressed +
                       " leftButton.isPressed=" + Mouse.current.leftButton.isPressed +
                       " mousePos=" + Mouse.current.position.ReadValue());
        }

        if (Mouse.current.rightButton.wasPressedThisFrame)
        {
            lastPanScreenPos = Mouse.current.position.ReadValue();
            isPanning = true;
            Debug.Log("[RTSCam] Right-click PRESSED - pan started");
        }
        else if (Mouse.current.rightButton.isPressed && isPanning)
        {
            Vector2 current = Mouse.current.position.ReadValue();
            Vector2 delta = current - lastPanScreenPos;
            Debug.Log("[RTSCam] Dragging - delta=" + delta + " cameraY=" + transform.position.y);
            Pan(delta);
            lastPanScreenPos = current;
        }
        else if (Mouse.current.rightButton.wasReleasedThisFrame)
        {
            isPanning = false;
            Debug.Log("[RTSCam] Right-click RELEASED - pan stopped");
        }

        float scroll = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            Zoom(-scroll * editorScrollZoomSpeed * Time.deltaTime);
        }
    }
#endif

    private void Pan(Vector2 screenDelta)
    {
        // Scale pan speed with current camera height so it feels consistent
        // whether zoomed in or out.
        float heightScale = transform.position.y / minHeight;
        Vector3 move = (rightFlat * -screenDelta.x + forwardFlat * -screenDelta.y)
                        * panSpeed * heightScale * Time.deltaTime;

        Vector3 newPos = transform.position + move;
        newPos.x = Mathf.Clamp(newPos.x, panBoundsX.x, panBoundsX.y);
        newPos.z = Mathf.Clamp(newPos.z, panBoundsZ.x, panBoundsZ.y);
        transform.position = newPos;
    }

    private void Zoom(float delta)
    {
        Vector3 pos = transform.position;
        pos.y = Mathf.Clamp(pos.y + delta, minHeight, maxHeight);
        transform.position = pos;
    }
}