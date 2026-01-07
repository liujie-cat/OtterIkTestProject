using UnityEngine;

/// <summary>
/// Screen-centered movement input with 3-zone intent:
/// - Inner zone: aim/turn only (minimal translation)
/// - Mid zone: swim (chase carrot but never catch)
/// - Outer zone: sprint (max speed, clamp input)
///
/// IMPORTANT:
/// - Input is measured relative to SCREEN CENTER (pixels).
/// - Carrot is anchored around the RING CENTER WORLD position (screen center projected to plane),
///   so the otter can drift away from ring center naturally.
/// </summary>
[DefaultExecutionOrder(200)]
public class MovementController : MonoBehaviour
{
    public enum MoveZone { None, Aim, Swim, Sprint }

    [Header("References")]
    [SerializeField] private Camera inputCamera;
    [SerializeField] private DualRingUIController ui;

    [Header("Plane (Water Surface)")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Speeds")]
    [SerializeField] private float maxSwimSpeed = 5f;
    [SerializeField] private float maxSprintSpeed = 8f;

    [Header("Acceleration / Turning")]
    [SerializeField] private float acceleration = 14f;
    [SerializeField] private float turnRateDeg = 360f;
    [SerializeField] private float sprintTurnRateDeg = 480f;

    [Header("Carrot (Chase Target)")]
    [Tooltip("Carrot world radius used in Aim zone (small, stable).")]
    [SerializeField] private float carrotRadiusAim = 1.0f;

    [Tooltip("Max carrot radius in Swim (at outer ring).")]
    [SerializeField] private float carrotRadiusSwimMax = 3.0f;

    [Tooltip("Carrot radius in Sprint (usually same as SwimMax).")]
    [SerializeField] private float carrotRadiusSprint = 3.0f;

    [Tooltip("In Swim/Sprint, keep carrot at least this far ahead of the otter (never catch).")]
    [SerializeField] private float keepDistance = 1.2f;

    [Tooltip("How quickly carrot follows input changes.")]
    [SerializeField] private float carrotFollow = 18f;

    [Tooltip("How quickly velocity vector aligns toward desired velocity.")]
    [SerializeField] private float velocityDirectionLerp = 10f;

    [Header("Input Smoothing (optional)")]
    [Tooltip("0 disables. Higher reduces jitter but adds input lag.")]
    [SerializeField] private float inputSmoothing = 0f;

    [Header("Debug")]
    [SerializeField] private bool debugDraw = false;

    private bool isDragging;
    private Vector2 dirScreen = Vector2.up;
    private float radiusPx;

    private Vector2 dirScreenSm = Vector2.up;
    private float radiusPxSm;

    private MoveZone zone = MoveZone.None;
    private float currentSpeed;
    private Vector3 velocity;

    private Vector3 ringCenterWorld;
    private Vector3 carrotWorld;
    private bool carrotInitialized;

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : transform.position.y;

    private void Start()
    {
        if (inputCamera == null) inputCamera = Camera.main;
        if (ui == null) ui = FindObjectOfType<DualRingUIController>();

        ringCenterWorld = GetRingCenterWorld();   // <-- ONLY ONE definition exists below
        carrotWorld = ringCenterWorld;
        carrotInitialized = true;
    }

    private void LateUpdate()
    {
        HandleInput();
        ApplyMovement();

        if (debugDraw) DrawDebug();
    }

    private void HandleInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            currentSpeed = 0f;
            velocity = Vector3.zero;
        }

        if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
            zone = MoveZone.None;
            currentSpeed = 0f;
            velocity = Vector3.zero;
            radiusPx = 0f;
            radiusPxSm = 0f;
            ui?.HideArrow();
            return;
        }

        if (!isDragging)
            return;

        Vector2 center = (ui != null) ? ui.ScreenCenterPx : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Vector2 mouse = Input.mousePosition;
        Vector2 delta = mouse - center;

        float rawR = delta.magnitude;
        Vector2 rawDir = rawR > 1e-3f ? (delta / rawR) : Vector2.up;

        if (inputSmoothing > 0f)
        {
            float k = 1f - Mathf.Exp(-inputSmoothing * Time.deltaTime);

            dirScreenSm = Vector2.Lerp(dirScreenSm, rawDir, k);
            if (dirScreenSm.sqrMagnitude > 1e-6f) dirScreenSm.Normalize();

            radiusPxSm = Mathf.Lerp(radiusPxSm, rawR, k);

            dirScreen = dirScreenSm;
            radiusPx = radiusPxSm;
        }
        else
        {
            dirScreen = rawDir;
            radiusPx = rawR;

            dirScreenSm = rawDir;
            radiusPxSm = rawR;
        }

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

        float clampedPx = Mathf.Clamp(radiusPx, 0f, outerPx);

        if (clampedPx <= innerPx) zone = MoveZone.Aim;
        else if (clampedPx < outerPx) zone = MoveZone.Swim;
        else zone = MoveZone.Sprint;

        ui?.UpdateArrowFromScreen(dirScreen, clampedPx);
    }

    private void ApplyMovement()
    {
        if (!isDragging) return;
        if (inputCamera == null) return;

        ringCenterWorld = GetRingCenterWorld();

        float innerPx = (ui != null) ? ui.InnerRadiusPx : 120f;
        float outerPx = (ui != null) ? ui.OuterRadiusPx : 260f;

        float rClamped = Mathf.Clamp(radiusPx, 0f, outerPx);

        float tBand = Mathf.InverseLerp(innerPx, outerPx, Mathf.Clamp(rClamped, innerPx, outerPx));
        tBand = tBand * tBand * (3f - 2f * tBand); // SmoothStep

        Vector3 worldDir = ScreenDirToWorldXZ(dirScreen);
        if (worldDir.sqrMagnitude < 1e-6f) return;

        float desiredCarrotRadius =
            zone == MoveZone.Aim ? carrotRadiusAim :
            zone == MoveZone.Swim ? Mathf.Lerp(carrotRadiusAim, carrotRadiusSwimMax, tBand) :
            carrotRadiusSprint;

        Vector3 desiredCarrot = ringCenterWorld + worldDir * desiredCarrotRadius;
        desiredCarrot.y = PlaneY;

        float dt = Time.deltaTime;

        if (!carrotInitialized)
        {
            carrotWorld = desiredCarrot;
            carrotInitialized = true;
        }
        else
        {
            float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, carrotFollow) * dt);
            carrotWorld = Vector3.Lerp(carrotWorld, desiredCarrot, k);
        }

        // Ensure "never catch" in Swim/Sprint
        if (zone == MoveZone.Swim || zone == MoveZone.Sprint)
        {
            Vector3 toCarrot = carrotWorld - transform.position;
            toCarrot.y = 0f;

            if (toCarrot.magnitude < keepDistance)
            {
                carrotWorld = transform.position + worldDir * keepDistance;
                carrotWorld.y = PlaneY;
            }
        }

        float targetSpeed = 0f;
        float turnRate = turnRateDeg;

        if (zone == MoveZone.Aim)
        {
            targetSpeed = 0f;
            turnRate = turnRateDeg;
        }
        else if (zone == MoveZone.Swim)
        {
            targetSpeed = maxSwimSpeed * tBand;
            turnRate = turnRateDeg;
        }
        else
        {
            targetSpeed = maxSprintSpeed;
            turnRate = sprintTurnRateDeg;
        }

        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, acceleration * dt);

        Vector3 desiredMoveDir = carrotWorld - transform.position;
        desiredMoveDir.y = 0f;
        desiredMoveDir = desiredMoveDir.sqrMagnitude > 1e-6f ? desiredMoveDir.normalized : worldDir;

        Vector3 desiredVel = desiredMoveDir * currentSpeed;

        if (velocityDirectionLerp > 0f)
        {
            float kv = 1f - Mathf.Exp(-velocityDirectionLerp * dt);
            velocity = Vector3.Lerp(velocity, desiredVel, kv);
        }
        else
        {
            velocity = desiredVel;
        }

        // Translate only in Swim/Sprint
        if (zone == MoveZone.Swim || zone == MoveZone.Sprint)
            transform.position += velocity * dt;

        // Rotate toward velocity direction (or desiredMoveDir)
        Vector3 faceDir = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : desiredMoveDir;
        faceDir.y = 0f;

        if (faceDir.sqrMagnitude > 1e-6f)
        {
            Quaternion targetRot = Quaternion.LookRotation(faceDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnRate * dt);
        }
    }

    /// <summary>
    /// SINGLE, unique definition of GetRingCenterWorld().
    /// If DualRingUIController exists, use its computed world center.
    /// Otherwise raycast from screen center onto the water plane.
    /// </summary>
    private Vector3 GetRingCenterWorld()
    {
        if (ui != null)
            return ui.GetRingCenterWorldOnPlane();

        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Ray ray = inputCamera.ScreenPointToRay(center);

        Plane plane = new Plane(Vector3.up, new Vector3(0f, PlaneY, 0f));
        if (plane.Raycast(ray, out float hit))
            return ray.GetPoint(hit);

        return new Vector3(transform.position.x, PlaneY, transform.position.z);
    }

    private Vector3 ScreenDirToWorldXZ(Vector2 dir)
    {
        // Same fix: use camera.up (screen Y) instead of camera.forward for top-down.
        Vector3 camRight = inputCamera.transform.right; camRight.y = 0f;
        Vector3 camUp    = inputCamera.transform.up;    camUp.y = 0f;

        if (camRight.sqrMagnitude < 1e-6f) camRight = Vector3.right;
        if (camUp.sqrMagnitude < 1e-6f)    camUp    = Vector3.forward;

        camRight.Normalize();
        camUp.Normalize();

        Vector3 w = camRight * dir.x + camUp * dir.y;
        w.y = 0f;

        return w.sqrMagnitude > 1e-6f ? w.normalized : Vector3.forward;
    }

    private void DrawDebug()
    {
        Debug.DrawRay(ringCenterWorld, Vector3.up * 0.4f, Color.cyan);
        Debug.DrawLine(ringCenterWorld, carrotWorld, Color.yellow);
        Debug.DrawRay(transform.position, velocity, Color.green);
        Debug.DrawRay(carrotWorld, Vector3.up * 0.25f, Color.yellow);
    }

    // ===== Exposed for other systems (IK / camera) =====
    public bool IsDragging() => isDragging;
    public MoveZone GetZone() => zone;
    public bool IsSprinting() => zone == MoveZone.Sprint;
    public float GetCurrentSpeed() => currentSpeed;
    public Vector3 GetVelocity() => velocity;
    public Vector3 GetCarrotWorld() => carrotWorld;
    public Vector3 GetRingCenterWorldPublic() => ringCenterWorld;
}
