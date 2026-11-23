using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// The player's controls (行动操作): the gestures (ControlGestures) of the go, back and dive keys shift the gears
    /// (GearMachine), which set the gait, heading and speed the brain swims to; a jump, each shift, picking the otter
    /// up or letting it go, the dives and the duck dive's kick go out as Control events. The go key picks it up and
    /// shifts up, held cruising it sprints, and in the dolphin wave it jumps; the back key shifts down and, parked,
    /// lets it go, and held it brakes to a stop and lets it go. The mouse is the stick, and only that: picked up, the
    /// pointer outside the ring round the otter is its guide point, which the head looks at and the body turns after;
    /// its buttons are left to other things. The dive key sends it under along its way through the depth (DivePath),
    /// deeper while held, and a tap under water brings it back up; both feet kick as the duck dive's hips go over.
    /// Turns the body toward the heading at the brain's yaw rate target and brakes it while the back key is held.
    /// </summary>
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public class OtterControls : MonoBehaviour
    {
        /// <summary>Stands in for the mouse and keys (scripted playback, tests): the go, back and dive keys, and the pointer's screen position in pixels.</summary>
        public interface IInput
        {
            bool Go { get; }
            bool Back { get; }
            bool Dive { get; }
            Vector2 ScreenPosition { get; }
        }

        [Tooltip("Picks the otter up, shifts up, held cruising sprints, and in the dolphin wave jumps.")]
        public KeyCode goKey = KeyCode.W;
        [Tooltip("Shifts down, parked lets the otter go; held, brakes to a stop and lets it go.")]
        public KeyCode backKey = KeyCode.S;
        [Tooltip("Sends the otter under (a duck dive); held, it swims deeper and let go keeps its depth; tapped under water, it comes back up.")]
        public KeyCode diveKey = KeyCode.Space;
        [Tooltip("The camera the pointer is read through. Empty: the one drawn last onto the screen, which is what the player sees (not necessarily the main camera).")]
        public Camera inputCamera;
        public OtterLocomotionBrain brain;
        public StrokePropulsion strokes;
        [Tooltip("The ring round the otter, from the middle of its back (body lengths): picked up, the pointer guides it from outside it, and inside it points nowhere.")]
        public float ringRadiusBL = 0.6f;
        [Tooltip("Once inside the ring, how far past it the pointer has to go to guide again, as a share of its radius: the ring goes with the body as it turns, drifts and stands up, which would flick a pointer resting at its edge in and out.")]
        public float ringMargin = 0.15f;
        public Gear initialGear = Gear.Park;
        public GestureSettings gestures = new GestureSettings();
        public GearSettings gears = new GearSettings();
        public DiveSettings dive = new DiveSettings();

        [Header("Body")]
        [Tooltip("Time for the body's turning to follow the brain's yaw rate target (s).")]
        public float turnResponse = 0.1f;
        [Tooltip("Deceleration while the back key is held (body lengths/s^2).")]
        public float brakeBL = 4f;

        [Header("Debug")]
        public bool logEvents = true;

        public IInput InputOverride { get; set; }
        public GearMachine Gears => machine;
        /// <summary>The way the body swims through the depth, stepped each frame after the gears.</summary>
        public DivePath DivePath => divePath;
        /// <summary>Kicks, jumps, gear shifts, picking up and letting go, as they happen.</summary>
        public event Action<ControlEvent> Control;
        public event Action<Gesture> Gestured;
        /// <summary>The pointer at PointerY, and whether it is on the otter (inside the ring, or not yet ringMargin past it since), this frame.</summary>
        public Vector3 PointerPoint { get; private set; }
        public bool PointerOverOtter { get; private set; }
        public bool GoHeld { get; private set; }
        public bool BackHeld { get; private set; }
        public bool DiveHeld { get; private set; }
        /// <summary>The camera the pointer is read through: inputCamera, or the one drawn last onto the screen.</summary>
        public Camera PointerCamera
        {
            get
            {
                if (inputCamera != null) return inputCamera;
                if (screenCamera == null || !screenCamera.isActiveAndEnabled)
                {
                    screenCamera = null;
                    foreach (var c in Camera.allCameras)
                        if (c.targetTexture == null && (screenCamera == null || c.depth > screenCamera.depth)) screenCamera = c;
                }
                return screenCamera;
            }
        }
        /// <summary>
        /// The level the pointer is read on, and the ring and guide drawn: through the middle of the otter's back, where
        /// it is seen. It rides well above the water, and under a camera looking down at a slant a press on its back
        /// would land on the water beside it.
        /// </summary>
        public float PointerY => transform.position.y + backHeight;
        /// <summary>The middle of its back, at the pointer's level: the ring's centre.</summary>
        public Vector3 GrabPoint => chest != null && hip != null
            ? Flat(0.5f * (chest.position + hip.position)) + Vector3.up * PointerY
            : Flat(transform.position) + Vector3.up * PointerY;
        public float RingRadius => ringRadiusBL * BodyLength;
        /// <summary>The head, at the pointer's level: where the guide is seen from.</summary>
        public Vector3 HeadPoint => (head != null ? Flat(head.position) : Flat(transform.position + transform.forward * (0.4f * BodyLength))) + Vector3.up * PointerY;
        public float BodyLength =>
            brain != null && brain.BodyLength > 0f ? brain.BodyLength : brain != null ? brain.fallbackBodyLength : 0.4f;

        private readonly ControlGestures input = new ControlGestures();
        private readonly GearMachine machine = new GearMachine();
        private readonly DivePath divePath = new DivePath();
        private readonly List<Gesture> gestureList = new List<Gesture>();
        private readonly List<ControlEvent> events = new List<ControlEvent>();
        private Rigidbody body;
        private Water water;
        private Camera screenCamera;
        private Transform head, chest, hip;
        private float backHeight;
        private Vector3 lastPosition;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            if (body != null)
            {
                // held at its height until Buoyancy floats it; no pitching or rolling of the physics body
                body.useGravity = false;
                body.constraints = (body.constraints | RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ)
                                   & ~RigidbodyConstraints.FreezeRotationY;
            }
        }

        // on Start: when added while the scene loads, the skin is not posed yet, and the brain may come after this
        private void Start()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (strokes == null) strokes = GetComponent<StrokePropulsion>();
            water = FindFirstObjectByType<Water>();
            head = OtterRig.FindActiveBone(transform, OtterRig.Head);
            chest = OtterRig.FindActiveBone(transform, OtterRig.Chest);
            hip = OtterRig.FindActiveBone(transform, OtterRig.Hip);
            // once, at rest: the pointer's level should not bob with the strokes
            backHeight = chest != null && hip != null ? 0.5f * (chest.position.y + hip.position.y) - transform.position.y : 0f;
        }

        private void OnEnable()
        {
            if (body == null) return;
            input.Reset();
            divePath.Reset();
            machine.Reset(Body(), initialGear);
            PointerPoint = body.position + transform.forward;
            lastPosition = body.position;
        }

        private void Update()
        {
            if (body == null) return;
            Vector2 screen;
            bool goDown = false, backDown = false, diveDown = false;
            if (InputOverride != null)
            {
                GoHeld = InputOverride.Go;
                BackHeld = InputOverride.Back;
                DiveHeld = InputOverride.Dive;
                screen = InputOverride.ScreenPosition;
            }
            else
            {
                GoHeld = Input.GetKey(goKey);
                BackHeld = Input.GetKey(backKey);
                DiveHeld = Input.GetKey(diveKey);
                goDown = Input.GetKeyDown(goKey);
                backDown = Input.GetKeyDown(backKey);
                diveDown = Input.GetKeyDown(diveKey);
                screen = Input.mousePosition;
            }
            if (TryPointerPoint(screen, out var point)) PointerPoint = point;
            PointerOverOtter = OverOtter(PointerPoint);

            gestureList.Clear();
            events.Clear();
            input.Step(gestures, Time.time, GoHeld, BackHeld, DiveHeld, gestureList, goDown, backDown, diveDown);
            machine.Step(gears, gestureList, Time.time, PointerPoint, PointerOverOtter, Body(), events);
            StepDive();
            foreach (var g in gestureList) Gestured?.Invoke(g);
            foreach (var e in events)
            {
                if (logEvents) Debug.Log($"[OtterControls] {e} at {Time.time:F2}s", this);
                Control?.Invoke(e);
            }
        }

        private void FixedUpdate()
        {
            if (body == null) return;
            float dt = Time.fixedDeltaTime;
            // toward the heading at the brain's yaw rate target, swinging round mostly while the turning strokes push
            float rate = brain != null && brain.isActiveAndEnabled ? brain.Demand.yawRateTarget * Mathf.Deg2Rad : 0f;
            if (strokes != null && strokes.isActiveAndEnabled) rate *= strokes.Turn;
            Vector3 w = body.angularVelocity;
            w.y = Mathf.Lerp(w.y, rate, 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, turnResponse)));
            body.angularVelocity = w;

            if (machine.Braking)
            {
                Vector3 v = body.velocity;
                v.y = 0f;
                float speed = v.magnitude;
                if (speed > 1e-4f) body.AddForce(-v / speed * Mathf.Min(speed, brakeBL * BodyLength * dt), ForceMode.VelocityChange);
            }
        }

        private GearBody Body()
        {
            Vector3 v = body.velocity;
            v.y = 0f;
            return new GearBody
            {
                position = body.position, head = HeadPoint, forward = transform.forward, speedBL = v.magnitude / BodyLength,
                underwater = divePath.Under, divingOrSurfacing = divePath.DivingOrSurfacing,
            };
        }

        // the way through the depth on by how far the body swam ahead this frame: not by a drift back, which would run
        // the way on ahead of the body, nor by a jump of the body (placed anew)
        private void StepDive()
        {
            Vector3 step = Flat(body.position - lastPosition);
            lastPosition = body.position;
            float moved = step.magnitude > 0.5f ? 0f : Mathf.Max(0f, Vector3.Dot(step, Flat(transform.forward).normalized));
            float floor = water != null ? water.SurfaceY - water.FloorY : float.PositiveInfinity;
            float supine = brain != null ? brain.Posture.SupineFraction : 0f;
            if (divePath.Step(dive, machine.Diving, machine.Descending, moved, supine, floor, Time.deltaTime))
                events.Add(new ControlEvent { kind = ControlEventKind.Kick, time = Time.time, strength = dive.plungeKick });
        }

        private bool TryPointerPoint(Vector2 screen, out Vector3 point)
        {
            point = default;
            var cam = PointerCamera;
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(screen);
            if (!new Plane(Vector3.up, new Vector3(0f, PointerY, 0f)).Raycast(ray, out float distance)) return false;
            point = ray.GetPoint(distance);
            return true;
        }

        private bool OverOtter(Vector3 point) => Flat(point - GrabPoint).magnitude <= RingRadius * (PointerOverOtter ? 1f + ringMargin : 1f);

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
