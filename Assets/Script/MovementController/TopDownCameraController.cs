using UnityEngine;

/// <summary>
/// Top-down orthographic follow camera with optional velocity + intent look-ahead.
/// Works great with "screen-centered ring" because otter can drift off center naturally.
/// </summary>
[RequireComponent(typeof(Camera))]
public class TopDownCameraController : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private MovementController movement;

    [Header("Follow")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 10f, 0f);
    [SerializeField] private float followSpeed = 10f;

    [Header("Look-ahead: Velocity")]
    [SerializeField] private float velLookAheadDistance = 1.0f;
    [SerializeField] private float velLookAheadLerp = 10f;

    [Header("Look-ahead: Intent (Carrot)")]
    [SerializeField] private float intentLookAheadDistance = 0.8f;
    [SerializeField] private float intentLookAheadLerp = 10f;

    [Header("Sprint Tuning")]
    [SerializeField] private float sprintVelLookAheadMultiplier = 1.3f;
    [SerializeField] private float sprintIntentLookAheadMultiplier = 1.3f;
    [SerializeField] private float sprintFollowSpeedMultiplier = 0.85f; // a bit looser feels heavier

    [Header("Orthographic")]
    [SerializeField] private bool forceOrthographic = true;
    [SerializeField] private bool forceOrthoSize = false;
    [SerializeField] private float orthographicSize = 5f;

    private Camera cam;
    private Vector3 velLookAheadCurrent;
    private Vector3 intentLookAheadCurrent;

    private void Awake()
    {
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (forceOrthographic)
            cam.orthographic = true;

        if (forceOrthoSize)
            cam.orthographicSize = orthographicSize;

        if (movement == null && target != null)
            movement = target.GetComponent<MovementController>();
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;
        bool sprinting = movement != null && movement.IsSprinting();

        float fs = followSpeed * (sprinting ? sprintFollowSpeedMultiplier : 1f);

        // Velocity look-ahead
        Vector3 desiredVelLA = Vector3.zero;
        float velLA = velLookAheadDistance * (sprinting ? sprintVelLookAheadMultiplier : 1f);

        if (movement != null && velLA > 0f)
        {
            Vector3 v = movement.GetVelocity();
            v.y = 0f;
            if (v.sqrMagnitude > 0.01f)
                desiredVelLA = v.normalized * velLA;
        }

        float kVel = 1f - Mathf.Exp(-Mathf.Max(0.01f, velLookAheadLerp) * dt);
        velLookAheadCurrent = Vector3.Lerp(velLookAheadCurrent, desiredVelLA, kVel);

        // Intent look-ahead (toward carrot)
        Vector3 desiredIntentLA = Vector3.zero;
        float intentLA = intentLookAheadDistance * (sprinting ? sprintIntentLookAheadMultiplier : 1f);

        if (movement != null && intentLA > 0f)
        {
            Vector3 toCarrot = movement.GetCarrotWorld() - target.position;
            toCarrot.y = 0f;
            if (toCarrot.sqrMagnitude > 0.01f)
                desiredIntentLA = toCarrot.normalized * intentLA;
        }

        float kIntent = 1f - Mathf.Exp(-Mathf.Max(0.01f, intentLookAheadLerp) * dt);
        intentLookAheadCurrent = Vector3.Lerp(intentLookAheadCurrent, desiredIntentLA, kIntent);

        Vector3 desiredPos = target.position + offset + velLookAheadCurrent + intentLookAheadCurrent;

        float kFollow = 1f - Mathf.Exp(-Mathf.Max(0.01f, fs) * dt);
        transform.position = Vector3.Lerp(transform.position, desiredPos, kFollow);
    }
}
