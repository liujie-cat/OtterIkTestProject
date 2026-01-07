using UnityEngine;

/// <summary>
/// Creates/updates a Head IK guide transform.
/// In this new system, head guide follows MovementController's CARROT target (world).
/// This matches "head leads, body follows" behavior.
///
/// - Dragging: guide = carrot (+ optional extra lead)
/// - Idle: guide sits a little forward of the head anchor (or otter root)
/// </summary>
public class HeadIKGuideFromArrow : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MovementController movement;
    [SerializeField] private Transform headAnchorOverride;

    [Header("Guide Transform")]
    [SerializeField] private Transform headIKGuide;

    [Header("Plane (Water Surface)")]
    [SerializeField] private bool useFixedPlaneY = true;
    [SerializeField] private float fixedPlaneY = 0f;

    [Header("Behavior")]
    [Tooltip("Extra lead in world units along movement direction (small, like 0~0.2).")]
    [SerializeField] private float extraLead = 0.0f;

    [Tooltip("When not dragging, place guide in front of anchor by this world distance (0 disables).")]
    [SerializeField] private float idleForward = 0.5f;

    [Header("Smoothing")]
    [SerializeField] private float positionLerpSpeed = 18f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoRadius = 0.08f;

    private float PlaneY => useFixedPlaneY ? fixedPlaneY : (movement != null ? movement.transform.position.y : 0f);

    private void Awake()
    {
        EnsureRefs();
        EnsureGuide();
    }

    private void Update()
    {
        EnsureRefs();
        EnsureGuide();
        if (movement == null || headIKGuide == null) return;

        Transform anchor = headAnchorOverride != null ? headAnchorOverride : movement.transform;

        if (!movement.IsDragging())
        {
            if (idleForward <= 0f)
            {
                headIKGuide.gameObject.SetActive(false);
                return;
            }

            Vector3 f = anchor.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();

            Vector3 idlePos = anchor.position + f * idleForward;
            idlePos.y = PlaneY;

            headIKGuide.position = SmoothMove(headIKGuide.position, idlePos);
            headIKGuide.gameObject.SetActive(true);
            return;
        }

        Vector3 carrot = movement.GetCarrotWorld();
        Vector3 dir = carrot - anchor.position;
        dir.y = 0f;

        Vector3 lead = Vector3.zero;
        if (dir.sqrMagnitude > 1e-6f)
            lead = dir.normalized * extraLead;

        Vector3 targetPos = carrot + lead;
        targetPos.y = PlaneY;

        headIKGuide.position = SmoothMove(headIKGuide.position, targetPos);
        headIKGuide.gameObject.SetActive(true);
    }

    private void EnsureRefs()
    {
        if (movement == null)
            movement = FindObjectOfType<MovementController>();
    }

    private void EnsureGuide()
    {
        if (headIKGuide != null) return;

        GameObject go = new GameObject("HeadIK_Guide");
        go.transform.SetParent(transform, worldPositionStays: true);

        Vector3 p = transform.position;
        p.y = PlaneY;
        go.transform.position = p;

        headIKGuide = go.transform;
    }

    private Vector3 SmoothMove(Vector3 current, Vector3 target)
    {
        if (!Application.isPlaying || positionLerpSpeed <= 0f) return target;
        float k = 1f - Mathf.Exp(-positionLerpSpeed * Time.deltaTime);
        return Vector3.Lerp(current, target, k);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (headIKGuide == null) return;

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
        Gizmos.DrawSphere(headIKGuide.position, gizmoRadius);
        Gizmos.DrawLine(headIKGuide.position, headIKGuide.position + Vector3.up * 0.35f);
    }

    public Transform GetHeadIKGuide() => headIKGuide;
}
