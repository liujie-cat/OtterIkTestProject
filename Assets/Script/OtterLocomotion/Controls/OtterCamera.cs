using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// The camera looking down on the otter: follows it without turning, more stiffly the faster it swims, and keeps
    /// it within a small circle round the middle of the screen.
    /// </summary>
    [DefaultExecutionOrder(500)]
    [RequireComponent(typeof(Camera))]
    public class OtterCamera : MonoBehaviour
    {
        [Tooltip("Found in the scene when empty.")]
        public OtterControls controls;
        [Tooltip("Where the camera sits from the otter (m).")]
        public Vector3 offset = new Vector3(0f, 10f, 0f);
        [Tooltip("How quickly the camera follows at rest, cruising and sprinting (1/s).")]
        public float restSharpness = 13.125f, cruiseSharpness = 18.75f, sprintSharpness = 24.375f;
        [Tooltip("Moves of the otter smaller than this are not followed, so the view does not jitter at rest (m).")]
        public float deadZone = 0.03f;
        [Tooltip("Radius round the middle of the screen the otter is kept within (px).")]
        public float keepWithinPx = 67.5f;
        [Tooltip("Time to bring the otter back within that circle (s).")]
        public float keepTime = 0.06f;

        private Camera view;
        private Rigidbody body;
        private Vector3 followVelocity, keepVelocity;
        private bool placed;

        private void Awake() => view = GetComponent<Camera>();

        private void LateUpdate()
        {
            // found again when the controls are swapped for fresh ones (scripted playback)
            if (controls == null) controls = FindFirstObjectByType<OtterControls>();
            if (controls == null) return;
            if (body == null) body = controls.GetComponent<Rigidbody>();
            float dt = Time.deltaTime;
            Vector3 otter = controls.transform.position;
            Vector3 desired = otter + offset;
            if (!placed)
            {
                transform.position = desired;
                placed = true;
            }
            if (Vector3.ProjectOnPlane(desired - transform.position, Vector3.up).magnitude < deadZone) desired = transform.position;

            float speed = body != null ? Vector3.ProjectOnPlane(body.velocity, Vector3.up).magnitude : 0f;
            float cruise = Mathf.Max(0.01f, controls.gears.cruiseSpeedBL * controls.BodyLength);
            float sprint = Mathf.Max(cruise, controls.gears.sprintSpeedBL * controls.BodyLength);
            float sharpness = Mathf.Lerp(Mathf.Lerp(restSharpness, cruiseSharpness, speed / cruise), sprintSharpness, Mathf.InverseLerp(cruise, sprint, speed));
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref followVelocity, 1f / Mathf.Max(0.01f, sharpness), Mathf.Infinity, dt);

            Vector2 middle = view.pixelRect.center;
            Vector2 seen = view.WorldToScreenPoint(otter);
            Vector2 off = seen - middle;
            float radius = Mathf.Max(1e-3f, keepWithinPx);
            if (off.magnitude <= radius) return;
            var level = new Plane(Vector3.up, otter);
            Vector3 correction = Vector3.ProjectOnPlane(OnLevel(level, seen) - OnLevel(level, middle + off * (radius / off.magnitude)), Vector3.up);
            transform.position = Vector3.SmoothDamp(transform.position, transform.position + correction, ref keepVelocity, Mathf.Max(1e-4f, keepTime), Mathf.Infinity, dt);
        }

        private Vector3 OnLevel(Plane level, Vector2 screen)
        {
            var ray = view.ScreenPointToRay(screen);
            return level.Raycast(ray, out float distance) ? ray.GetPoint(distance) : ray.origin;
        }
    }
}
