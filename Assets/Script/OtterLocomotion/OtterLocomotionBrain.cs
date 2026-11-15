using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace OtterLocomotion
{
    /// <summary>
    /// Runs the locomotion pipeline once per physics step, in causal order:
    /// intent + measured state -> posture -> demand -> allocation -> stroke rhythm (-> forces, in a later stage).
    /// Intent comes from MovementControllerRB's ring input. Nothing here moves the body yet: it computes what the body
    /// would need, which limb would supply it and each limb's stroke cycle (drawn by LimbStrokeAnimator), so it can
    /// run alongside the existing controller.
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
        [Tooltip("Yaw inertia (kg m^2); 0 = estimated from the measured body length as a uniform rod, m L^2 / 12.")]
        public float yawInertiaOverride = 0f;
        [Tooltip("Nose-to-rump length as a multiple of the head-joint-to-tail-root distance.")]
        public float headLengthRatio = 1.4f;
        [Tooltip("Time for the water alone to slow a spin to 1/e (s); sets the yaw damping from the yaw inertia.")]
        public float yawDampingTime = 0.4f;

        [Header("Limbs")]
        public ActuatorSettings actuatorSettings = new ActuatorSettings();
        public AllocatorSettings allocatorSettings = new AllocatorSettings();
        public RhythmSettings rhythmSettings = new RhythmSettings();

        [Header("Debug")]
        public bool drawGizmos = true;
        public bool logPostureChanges = true;
        [Tooltip("Gizmo arrow length per newton (m/N).")]
        public float forceGizmoScale = 0.05f;
        [Tooltip("Gizmo arc degrees per N·m.")]
        public float momentGizmoScale = 300f;

        public LocomotionIntent Intent { get; private set; }
        public BodyState State => estimator.State;
        public LocomotionDemand Demand => demandLayer.Current;
        public PostureMachine Posture => postureMachine;
        public BodyParameters Parameters { get; private set; }
        public ActuatorModel Actuators => actuators;
        public ControlAllocator Allocation => allocator;
        /// <summary>Each limb's stroke cycle, stepped after the allocation every physics step.</summary>
        public LimbRhythm Rhythm => rhythm;
        public bool HasLimbGeometry { get; private set; }
        /// <summary>Measured nose-to-rump length (m); 0 if the head or tail bone was not found.</summary>
        public float BodyLength { get; private set; }
        public LimbGeometry[] Geometry => geometry;
        public Vector3 CenterOfMass => centerOfMass;

        private readonly BodyStateEstimator estimator = new BodyStateEstimator();
        private readonly PostureMachine postureMachine = new PostureMachine();
        private readonly DemandLayer demandLayer = new DemandLayer();
        private readonly ActuatorModel actuators = new ActuatorModel();
        private readonly ControlAllocator allocator = new ControlAllocator(OtterRig.LimbCount * ActuatorModel.ColumnsPerLimb);
        private readonly LimbRhythm rhythm = new LimbRhythm();
        private readonly LimbGeometry[] geometry = new LimbGeometry[OtterRig.LimbCount];
        private Vector3 centerOfMass;
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

        // On Start rather than OnEnable: when added while the scene is still loading, the IK chains are not enabled yet.
        private void Start()
        {
            if (body != null) CaptureGeometry();
        }

        private void OnEnable()
        {
            if (body == null) return;
            estimator.Reset(body);
            postureMachine.Reset(postureSettings);
            demandLayer.Reset(estimator.State);
            allocator.Reset();
            rhythm.Reset();
        }

        // Body frame: x forward, y right, z up, metres from the rigidbody origin.
        private Vector3 ToBody(Vector3 world)
        {
            Vector3 l = Quaternion.Inverse(body.rotation) * (world - body.position);
            return new Vector3(l.z, l.x, l.y);
        }

        private Vector3 ToWorld(Vector3 b) => body.position + body.rotation * new Vector3(b.y, b.z, b.x);

        /// <summary>Shoulders, hips and limb lengths from the IK chains, and body length from head to tail root, as rigged (prone).</summary>
        private void CaptureGeometry()
        {
            int found = 0;
            Vector3 sum = Vector3.zero;
            var chains = OtterRig.FindLimbChains(transform);
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var ik = chains[i];
                if (ik == null) continue;
                Vector3 a = ik.root.bone.position, b = ik.mid.bone.position, c = ik.end.bone.position;
                geometry[i] = new LimbGeometry
                {
                    valid = true,
                    anchor = ToBody(a),
                    length = Vector3.Distance(a, b) + Vector3.Distance(b, c),
                };
                sum += geometry[i].anchor;
                found++;
            }
            HasLimbGeometry = found > 0;
            centerOfMass = found > 0 ? new Vector3(sum.x / found, 0f, sum.z / found) : Vector3.zero;
            if (found == 0)
            {
                var all = FindObjectsByType<AdvancedTwoBoneIK>(FindObjectsSortMode.None)
                    .Select(k => $"{k.name} (root bone {(k.root?.bone ? k.root.bone.name + " under " + k.root.bone.root.name : "none")}, active {k.isActiveAndEnabled})");
                Debug.LogWarning($"[OtterLocomotion] No limb IK chains under '{name}'; allocation disabled. Chains in scene: {string.Join("; ", all)}", this);
            }

            var head = OtterRig.FindActiveBone(transform, OtterRig.Head);
            var tail = OtterRig.FindActiveBone(transform, OtterRig.TailRoot);
            BodyLength = head != null && tail != null ? Vector3.Distance(head.position, tail.position) * headLengthRatio : 0f;
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
            var demand = demandLayer.Step(demandSettings, Intent, state, Parameters, speedCap, dt);

            if (!HasLimbGeometry) return;
            actuators.Build(actuatorSettings, geometry, centerOfMass, postureMachine.RollDeg, postureMachine.SupineFraction, Parameters.mass);
            allocator.Solve(allocatorSettings, actuators, demand, Parameters);
            rhythm.Step(rhythmSettings, allocator, demand.speedTarget, dt);
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
                reach = intentSource.GetReach01(),
            };
        }

        private BodyParameters ReadParameters()
        {
            float inertia = yawInertiaOverride > 0f ? yawInertiaOverride
                          : BodyLength > 0f ? body.mass * BodyLength * BodyLength / 12f
                          : body.inertiaTensor.y;
            return new BodyParameters
            {
                mass = body.mass,
                yawInertia = inertia,
                linearDrag = waterDrag != null ? waterDrag.Linear : 0f,
                quadraticDrag = waterDrag != null ? waterDrag.Quadratic : 0f,
                angularLinearDrag = inertia / Mathf.Max(0.05f, yawDampingTime),
            };
        }

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

            string allocation = "";
            if (HasLimbGeometry)
            {
                for (int i = 0; i < OtterRig.LimbCount; i++)
                {
                    if (!actuators.present[i]) continue;
                    var lf = allocator.limbs[i];
                    Vector3 p = ToWorld(actuators.points[i]);
                    Vector3 f = s.forward * lf.force.x + s.right * lf.force.y;
                    Handles.color = lf.saturated ? Color.red : OtterRig.IsFore((LimbId)i) ? new Color(0.45f, 0.8f, 1f) : new Color(1f, 0.7f, 0.3f);
                    Handles.DrawWireDisc(p, Vector3.up, 0.015f);
                    Handles.DrawLine(p, p + f * forceGizmoScale, 3f);
                }
                Vector3 a = allocator.Achieved;
                allocation = $"\nallocated Fx {a.x:F1}  Fy {a.y:F1}  Mz {a.z:F2}  ({allocator.SaturatedStrokes} strokes at limit)";
            }

            string flip = postureMachine.IsFlipping ? $" rolling {postureMachine.FlipProgress:P0}" : "";
            Handles.Label(o + Vector3.up * 0.3f,
                $"{postureMachine.Current}{flip}  roll {postureMachine.RollDeg:F0}\n" +
                $"{Intent.band}  u {s.surge:F2} -> {d.speedTarget:F2} (cap {d.speedCap:F2})\n" +
                $"heading err {d.headingErrorDeg:F0}  r {s.yawRate:F0} -> {d.yawRateTarget:F0} (lim {d.yawRateLimit:F0})\n" +
                $"demand  Fx {d.surgeForce:F1} N  Fy {d.swayForce:F1} N  Mz {d.yawMoment:F2} N·m  bank {d.bankTargetDeg:F0}" +
                allocation);
        }
#endif
    }
}
