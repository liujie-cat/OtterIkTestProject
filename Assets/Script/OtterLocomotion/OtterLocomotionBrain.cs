using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace OtterLocomotion
{
    /// <summary>
    /// Runs the locomotion pipeline once per physics step, in causal order:
    /// intent + measured state -> posture -> demand -> allocation -> stroke rhythm. Intent comes from OtterControls'
    /// gears. It does not push the body itself: the strokes it times (drawn by LimbStrokeAnimator) push it through
    /// the water (WebThrust), and OtterControls turns it at the yaw rate asked for here.
    /// </summary>
    [DefaultExecutionOrder(150)]
    [DisallowMultipleComponent]
    public class OtterLocomotionBrain : MonoBehaviour
    {
        [Header("References (found on this object when empty)")]
        public Rigidbody body;
        public OtterControls controls;
        public WaterDrag waterDrag;
        [Tooltip("Body length (m) assumed when the head or tail bone is not found.")]
        public float fallbackBodyLength = 0.4f;

        [Header("Kicks")]
        [Tooltip("A click's kick waits until the body faces within this angle (deg) of where it was clicked, or kickWait seconds at most.")]
        public float kickAlignDeg = 30f;
        public float kickWait = 0.6f;

        [Header("Posture")]
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
        private ControlEvent pendingKick;
        private bool kickPending;
        private float kickQueuedAt;
        private string shiftReason = "initial";

        private void Awake()
        {
            if (body == null) body = GetComponent<Rigidbody>();
            if (controls == null) controls = GetComponent<OtterControls>();
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
            if (controls != null) controls.Control += OnControl;
            if (body == null) return;
            estimator.Reset(body);
            postureMachine.Reset(GearMachine.PostureOf(controls != null ? controls.initialGear : Gear.Park));
            demandLayer.Reset(estimator.State);
            allocator.Reset();
            rhythm.Reset();
            kickPending = false;
        }

        private void OnDisable()
        {
            if (controls != null) controls.Control -= OnControl;
        }

        private void OnControl(ControlEvent e)
        {
            if (e.kind == ControlEventKind.Shift) shiftReason = e.reason;
            if (e.kind != ControlEventKind.Kick) return;
            pendingKick = e;
            kickPending = true;
            kickQueuedAt = Time.time;
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

        private void FixedUpdate()
        {
            if (body == null) return;
            float dt = Time.fixedDeltaTime;
            Parameters = ReadParameters();
            var state = estimator.Step(body, dt);
            Intent = ReadIntent();

            float length = Parameters.bodyLength;
            string reason = Intent.posture != postureMachine.Current ? $"{Intent.gear} ({shiftReason})" : null;
            postureMachine.Step(postureSettings, Intent.posture, reason, Demand.yawRateTarget, dt);

            float supine = postureMachine.SupineFraction;
            float riseRate = Mathf.Lerp(demandSettings.speedRiseRateBL, postureSettings.supineRiseRateBL, supine) * length;
            float kickSpeed = Mathf.Lerp(demandSettings.proneKickSpeedBL, demandSettings.supineKickSpeedBL, supine) * length;
            var demand = demandLayer.Step(demandSettings, Intent, state, Parameters, riseRate, kickSpeed, 2f * rhythmSettings.hindFullForce, dt);

            if (!HasLimbGeometry) return;
            float speedTargetBL = demand.speedTarget / length;
            actuators.Build(actuatorSettings, geometry, centerOfMass, postureMachine.RollDeg, postureMachine.SupineFraction, Parameters.mass, speedTargetBL);
            allocator.Solve(allocatorSettings, actuators, demand, Parameters);
            // a click's kick pushes off once the body faces where it was clicked
            if (kickPending && (Vector3.Angle(state.forward, pendingKick.direction) <= kickAlignDeg || Time.time - kickQueuedAt >= kickWait))
            {
                rhythm.Kick(pendingKick.strength);
                kickPending = false;
            }
            rhythm.Step(rhythmSettings, allocator, Intent.gait, dt);
        }

        private LocomotionIntent ReadIntent()
        {
            if (controls == null || !controls.isActiveAndEnabled)
                return new LocomotionIntent { gear = Gear.Park, gait = Gait.Rest, posture = postureMachine.Current, heading = estimator.State.forward };
            var g = controls.Gears;
            return new LocomotionIntent
            {
                gear = g.Gear,
                gait = g.Gait,
                posture = g.Posture,
                heading = g.BodyHeading,
                requestedSpeed = g.SpeedBL * Parameters.bodyLength,
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
                bodyLength = BodyLength > 0f ? BodyLength : fallbackBodyLength,
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
                $"{Intent.gear} {Intent.gait}  u {s.surge:F2} -> {d.speedTarget:F2} (asked {d.requestedSpeed:F2})\n" +
                $"heading err {d.headingErrorDeg:F0}  r {s.yawRate:F0} -> {d.yawRateTarget:F0} (lim {d.yawRateLimit:F0})\n" +
                $"demand  Fx {d.surgeForce:F1} N  Fy {d.swayForce:F1} N  Mz {d.yawMoment:F2} N·m  bank {d.bankTargetDeg:F0}" +
                allocation);
        }
#endif
    }
}
