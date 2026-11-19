using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// The player's controls (行动操作): the mouse's gestures (PointerGestures) shift the gears (GearMachine), which set
    /// the gait, heading and speed the brain swims to; a click's kick, a jump and each shift go out as Control events.
    /// A right click inside the ring round the otter picks it up and a left click inside it lets go; picked up, the
    /// pointer outside the ring is its guide point, which the head looks at and the body turns after. Everything else
    /// is done on the water. Turns the body toward the heading at the brain's yaw rate target and brakes it while the
    /// right button is held. Takes over from MovementControllerRB: that stays off, its ring UI hidden, and its getters
    /// report these controls to the older head, spine and camera scripts.
    /// </summary>
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public class OtterControls : MonoBehaviour, MovementControllerRB.ISteeringSource
    {
        /// <summary>Stands in for the mouse (scripted playback, tests): both buttons and the screen position in pixels.</summary>
        public interface IPointer
        {
            bool Left { get; }
            bool Right { get; }
            Vector2 ScreenPosition { get; }
        }

        [Tooltip("The camera the pointer is read through. Empty: the one drawn last onto the screen, which is what the player sees (not necessarily the main camera).")]
        public Camera inputCamera;
        public OtterLocomotionBrain brain;
        public StrokePropulsion strokes;
        public MovementControllerRB legacy;
        [Tooltip("The ring round the otter, from the middle of its back (body lengths): a press inside it lands on the otter, and selected, the pointer guides it from outside it.")]
        public float ringRadiusBL = 0.6f;
        public Gear initialGear = Gear.Park;
        public GestureSettings gestures = new GestureSettings();
        public GearSettings gears = new GearSettings();

        [Header("Body")]
        [Tooltip("Time for the body's turning to follow the brain's yaw rate target (s).")]
        public float turnResponse = 0.1f;
        [Tooltip("Deceleration while the right button is held (body lengths/s^2).")]
        public float brakeBL = 4f;

        [Header("Debug")]
        public bool logEvents = true;

        public IPointer PointerOverride { get; set; }
        public GearMachine Gears => machine;
        /// <summary>Kicks, jumps and gear shifts, as they happen.</summary>
        public event Action<ControlEvent> Control;
        public event Action<Gesture> Gestured;
        /// <summary>The pointer at PointerY, and whether it is on the otter (inside the ring), this frame.</summary>
        public Vector3 PointerPoint { get; private set; }
        public bool PointerOverOtter { get; private set; }
        public bool LeftHeld { get; private set; }
        public bool RightHeld { get; private set; }
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

        private readonly PointerGestures pointer = new PointerGestures();
        private readonly GearMachine machine = new GearMachine();
        private readonly List<Gesture> gestureList = new List<Gesture>();
        private readonly List<ControlEvent> events = new List<ControlEvent>();
        private Rigidbody body;
        private Camera screenCamera;
        private Transform head, chest, hip;
        private float backHeight;

        private float BodyLength =>
            brain != null && brain.BodyLength > 0f ? brain.BodyLength : brain != null ? brain.fallbackBodyLength : 0.4f;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            if (legacy == null) legacy = GetComponent<MovementControllerRB>();
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
            head = OtterRig.FindActiveBone(transform, OtterRig.Head);
            chest = OtterRig.FindActiveBone(transform, OtterRig.Chest);
            hip = OtterRig.FindActiveBone(transform, OtterRig.Hip);
            // once, at rest: the pointer's level should not bob with the strokes
            backHeight = chest != null && hip != null ? 0.5f * (chest.position.y + hip.position.y) - transform.position.y : 0f;
        }

        private void OnEnable()
        {
            if (body == null) return;
            pointer.Reset();
            machine.Reset(Body(), initialGear);
            PointerPoint = body.position + transform.forward;
            if (legacy != null)
            {
                legacy.SteeringSource = this;
                legacy.enabled = false;
                // only the ring: its object also carries the head IK guide and what moves it
                if (legacy.RingUI != null) legacy.RingUI.enabled = false;
            }
        }

        private void OnDisable()
        {
            if (legacy != null && ReferenceEquals(legacy.SteeringSource, this)) legacy.SteeringSource = null;
        }

        private void Update()
        {
            if (body == null) return;
            Vector2 screen;
            if (PointerOverride != null)
            {
                LeftHeld = PointerOverride.Left;
                RightHeld = PointerOverride.Right;
                screen = PointerOverride.ScreenPosition;
            }
            else
            {
                LeftHeld = Input.GetMouseButton(0);
                RightHeld = Input.GetMouseButton(1);
                screen = Input.mousePosition;
            }
            if (TryPointerPoint(screen, out var point)) PointerPoint = point;
            PointerOverOtter = OverOtter(PointerPoint);

            gestureList.Clear();
            events.Clear();
            pointer.Step(gestures, Time.time, LeftHeld, RightHeld, PointerPoint, PointerOverOtter, gestureList);
            machine.Step(gears, gestures, gestureList, Time.time, PointerPoint, PointerOverOtter, Body(), events);
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
            return new GearBody { position = body.position, head = HeadPoint, forward = transform.forward, speedBL = v.magnitude / BodyLength };
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

        private bool OverOtter(Vector3 point) => Flat(point - GrabPoint).magnitude <= RingRadius;

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        bool MovementControllerRB.ISteeringSource.Dragging => machine.Selected;
        MovementControllerRB.MoveZone MovementControllerRB.ISteeringSource.Zone => machine.Gear switch
        {
            Gear.Park => MovementControllerRB.MoveZone.Aim,
            Gear.Sprint => MovementControllerRB.MoveZone.Sprint,
            _ => MovementControllerRB.MoveZone.Swim,
        };
        Vector3 MovementControllerRB.ISteeringSource.Heading => machine.Heading;
        Vector3 MovementControllerRB.ISteeringSource.AimPoint => machine.AimPoint;
        float MovementControllerRB.ISteeringSource.AimBlend => machine.AimBlend;
        float MovementControllerRB.ISteeringSource.TargetSpeed => machine.SpeedBL * BodyLength;
        float MovementControllerRB.ISteeringSource.SwimSpeed => gears.cruiseSpeedBL * BodyLength;
        float MovementControllerRB.ISteeringSource.SprintSpeed => gears.sprintSpeedBL * BodyLength;
    }
}
