using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace OtterLocomotion
{
    /// <summary>
    /// Runs the locomotion pipeline once per physics step, in causal order:
    /// intent + measured state -> posture -> demand (-> allocation -> strokes -> forces, in later stages).
    /// Intent comes from MovementControllerRB's ring input. Nothing here moves the body yet: it only computes and
    /// draws what the body would need, so it can run alongside the existing controller.
    /// </summary>
    [DefaultExecutionOrder(150)]
    [DisallowMultipleComponent]
    public class OtterLocomotionBrain : MonoBehaviour
    {
        [Header("References (found on this object when empty)")]
        public Rigidbody body;
        public MovementControllerRB intentSource;
        public WaterDrag waterDrag;

        [Header("Posture")]
        public KeyCode postureToggleKey = KeyCode.F;
        public PostureSettings postureSettings = new PostureSettings();

        [Header("Demand")]
        public DemandSettings demandSettings = new DemandSettings();
        [Tooltip("Yaw inertia (kg m^2); 0 = the rigidbody's.")]
        public float yawInertiaOverride = 0f;

        [Header("Debug")]
        public bool drawGizmos = true;
        public bool logPostureChanges = true;
        [Tooltip("Gizmo arrow length per newton (m/N).")]
        public float forceGizmoScale = 0.05f;
        [Tooltip("Gizmo arc degrees per N·m.")]
        public float momentGizmoScale = 60f;

        public LocomotionIntent Intent { get; private set; }
        public BodyState State => estimator.State;
        public LocomotionDemand Demand => demandLayer.Current;
        public PostureMachine Posture => postureMachine;
        public BodyParameters Parameters { get; private set; }

        private readonly BodyStateEstimator estimator = new BodyStateEstimator();
        private readonly PostureMachine postureMachine = new PostureMachine();
        private readonly DemandLayer demandLayer = new DemandLayer();
        private bool toggleRequested;

        /// <summary>Same as pressing the posture key; for scripted input.</summary>
        public void RequestPostureToggle() => toggleRequested = true;

        private void Awake()
        {
            if (body == null) body = GetComponent<Rigidbody>();
            if (intentSource == null) intentSource = GetComponent<MovementControllerRB>();
            if (waterDrag == null) waterDrag = GetComponent<WaterDrag>();
            postureMachine.Changed += OnPostureChanged;
        }

        private void OnEnable()
        {
            if (body == null) return;
            estimator.Reset(body);
            postureMachine.Reset(postureSettings);
            demandLayer.Reset(estimator.State);
        }

        private void Update()
        {
            if (Input.GetKeyDown(postureToggleKey)) toggleRequested = true;
        }

        private void FixedUpdate()
        {
            if (body == null) return;
            float dt = Time.fixedDeltaTime;
            Parameters = ReadParameters();
            var state = estimator.Step(body, dt);
            Intent = ReadIntent();

            postureMachine.Step(postureSettings, Intent, state, toggleRequested, Demand.yawRateTarget, dt);
            toggleRequested = false;

            float supineCapped = Mathf.Min(Intent.requestedSpeed, postureSettings.supineMaxSpeed);
            float speedCap = Mathf.Lerp(Intent.requestedSpeed, supineCapped, postureMachine.SupineFraction);
            demandLayer.Step(demandSettings, Intent, state, Parameters, speedCap, dt);
        }

        private LocomotionIntent ReadIntent()
        {
            if (intentSource == null || !intentSource.IsDragging())
                return new LocomotionIntent { band = SpeedBand.Rest, heading = estimator.State.forward };
            var band = intentSource.GetZone() switch
            {
                MovementControllerRB.MoveZone.Aim => SpeedBand.Aim,
                MovementControllerRB.MoveZone.Swim => SpeedBand.Swim,
                MovementControllerRB.MoveZone.Sprint => SpeedBand.Sprint,
                _ => SpeedBand.Rest,
            };
            Vector3 dir = intentSource.GetWorldDir();
            dir.y = 0f;
            return new LocomotionIntent
            {
                band = band,
                heading = dir.sqrMagnitude > 1e-8f ? dir.normalized : estimator.State.forward,
                requestedSpeed = intentSource.GetTargetSpeed(),
            };
        }

        private BodyParameters ReadParameters() => new BodyParameters
        {
            mass = body.mass,
            yawInertia = yawInertiaOverride > 0f ? yawInertiaOverride : body.inertiaTensor.y,
            linearDrag = waterDrag != null ? waterDrag.Linear : 0f,
            quadraticDrag = waterDrag != null ? waterDrag.Quadratic : 0f,
            angularLinearDrag = waterDrag != null ? waterDrag.AngularLinear : 0f,
        };

        private void OnPostureChanged(Posture posture, string reason)
        {
            if (logPostureChanges) Debug.Log($"[OtterLocomotion] posture -> {posture} ({reason}) at {Time.time:F2}s", this);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !drawGizmos || body == null) return;
            var s = State;
            var d = Demand;
            Vector3 o = s.position + Vector3.up * 0.15f;

            Vector3 headingDir = Quaternion.Euler(0f, d.headingTargetDeg, 0f) * Vector3.forward;
            Handles.color = Color.cyan;
            Handles.DrawLine(o, o + headingDir * 0.8f, 2f);
            Handles.color = new Color(1f, 1f, 1f, 0.7f);
            Handles.DrawLine(o, o + s.forward * 0.6f);

            Handles.color = Color.green;
            Handles.DrawLine(o, o + s.forward * d.surgeForce * forceGizmoScale, 3f);
            Handles.color = Color.yellow;
            Handles.DrawLine(o, o + s.right * d.swayForce * forceGizmoScale, 3f);

            Handles.color = Color.magenta;
            Handles.DrawWireArc(o, Vector3.up, s.forward, Mathf.Clamp(d.yawMoment * momentGizmoScale, -270f, 270f), 0.35f, 2f);

            // bank bar: the end that dips is the side pressed into the water
            Vector3 bar = Quaternion.AngleAxis(-d.bankTargetDeg, s.forward) * s.right;
            Handles.color = new Color(1f, 0.55f, 0.1f);
            Handles.DrawLine(o - bar * 0.3f, o + bar * 0.3f, 3f);

            string flip = postureMachine.IsFlipping ? $" rolling {postureMachine.FlipProgress:P0}" : "";
            Handles.Label(o + Vector3.up * 0.3f,
                $"{postureMachine.Current}{flip}  roll {postureMachine.RollDeg:F0}\n" +
                $"{Intent.band}  u {s.surge:F2} -> {d.speedTarget:F2} (cap {d.speedCap:F2})\n" +
                $"heading err {d.headingErrorDeg:F0}  r {s.yawRate:F0} -> {d.yawRateTarget:F0} (lim {d.yawRateLimit:F0})\n" +
                $"Fx {d.surgeForce:F1} N  Fy {d.swayForce:F1} N  Mz {d.yawMoment:F2} N·m  bank {d.bankTargetDeg:F0}");
        }
#endif
    }
}
