using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion.Testing
{
    /// <summary>
    /// Scripted mouse input for repeatable locomotion tests. Each segment does one thing with the buttons, as the
    /// player would: a click, double click or long press on the water, steering the otter picked up (a right click on
    /// it, then the pointer as its guide), letting it go (a left click on it), a right tap or hold on the water;
    /// pointing toward a yaw relative to the otter's facing when playback started (+ = clockwise seen from above,
    /// i.e. right). Between pointing segments the pointer stays where it last pointed from the otter, as a still mouse
    /// does under the following camera. Feeds OtterControls through its pointer override, so its gestures and gears
    /// apply exactly as with the mouse. Locks the frame rate so runs repeat.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class OtterInputPlayback : MonoBehaviour, OtterControls.IPointer
    {
        public enum Action { Idle, Click, DoubleClick, Hold, Steer, LetGo, RightTap, RightHold }

        [Serializable]
        public struct Segment
        {
            public string name;
            public float duration;
            public Action action;
            [Tooltip("Where it points: yaw relative to the otter's facing at playback start (degrees, + = right).")]
            public float headingDeg;
            [Tooltip("How far from the otter it points (body lengths); 0 = the action's usual distance.")]
            public float distanceBL;

            public Segment(string name, float duration, Action action, float headingDeg = 0f, float distanceBL = 0f)
            {
                this.name = name;
                this.duration = duration;
                this.action = action;
                this.headingDeg = headingDeg;
                this.distanceBL = distanceBL;
            }

            public bool Points => action == Action.Click || action == Action.DoubleClick || action == Action.Hold || action == Action.Steer;
        }

        public OtterControls controls;
        [Tooltip("Empty = the built-in baseline script.")]
        public List<Segment> segments = new List<Segment>();
        [Tooltip("Fixed frames per second while playing back (Time.captureFramerate); 0 leaves the frame rate free.")]
        public int captureFramerate = 60;
        [Tooltip("Buttons up at the start of each segment (s), so its press is a new one; steering it while it is picked up already doesn't pick it up again.")]
        public float lead = 0.05f;
        [Tooltip("How long a click holds the button down, and the gap between a double click's two clicks (s).")]
        public float tap = 0.08f;
        public float gap = 0.1f;
        [Tooltip("Usual distance from the otter (body lengths) of a click or long press, and of the guide point while steering.")]
        public float clickDistanceBL = 2f;
        public float guideDistanceBL = 1.5f;

        public bool Left { get; private set; }
        public bool Right { get; private set; }
        public Vector2 ScreenPosition { get; private set; }
        public bool Finished { get; private set; }
        public int SegmentIndex { get; private set; } = -1;
        public float SegmentStartTime { get; private set; }
        public float StartYaw { get; private set; }
        public Segment CurrentSegment => SegmentIndex >= 0 && SegmentIndex < segments.Count ? segments[SegmentIndex] : default;
        public float TargetYaw(int index) => StartYaw + segments[index].headingDeg;

        private float startTime;
        private int previousCaptureFramerate;
        private Vector3 pointed;
        private bool pickUp;

        /// <summary>
        /// Every gear and gesture of the controls once: parked clicks, picking it up and steering it, the double clicks
        /// up to slow and to cruising, turns and clicks in each, the long-press sprint into the wave, a jump, the glide
        /// out of it, the right tap down, the brake to a stop and letting it go.
        /// </summary>
        public static List<Segment> BaselineScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: click ahead, one kick", 2.0f, Action.Click, 0f),
            new Segment("parked: click right 90, turn and kick", 2.5f, Action.Click, 90f),
            new Segment("parked: pick it up, steer left 90, small arc", 2.5f, Action.Steer, 0f),
            new Segment("parked: double click, slow", 3.0f, Action.DoubleClick, 0f),
            new Segment("slow: straight on the back", 2.5f, Action.Idle),
            new Segment("slow: steer right 90", 3.0f, Action.Steer, 90f),
            new Segment("slow: click left 45, kick", 2.0f, Action.Click, 45f),
            new Segment("slow: double click, cruise", 3.0f, Action.DoubleClick, 45f),
            new Segment("cruise: straight on the belly", 2.5f, Action.Idle),
            new Segment("cruise: steer left 90", 3.0f, Action.Steer, -45f),
            new Segment("cruise: long press, sprint", 3.5f, Action.Hold, -45f),
            new Segment("sprint: let go in the wave", 0.3f, Action.Idle),
            new Segment("sprint: double click, jump", 1.2f, Action.DoubleClick, -45f),
            new Segment("sprint: glide out of the wave", 3.0f, Action.Idle),
            new Segment("cruise: right tap, slow", 3.0f, Action.RightTap),
            new Segment("slow: right hold, brake", 2.0f, Action.RightHold),
            new Segment("parked: let go of it, rest", 1.5f, Action.LetGo),
        };

        /// <summary>Small (20°) and large (120° / 160°) turns each way in every gear, plus a gentle sweep in 15° steps cruising.</summary>
        public static List<Segment> TurnScript() => new List<Segment>
        {
            new Segment("parked: rest", 1.0f, Action.Idle),
            new Segment("parked: small left 20", 1.5f, Action.Steer, -20f),
            new Segment("parked: small right 20", 1.5f, Action.Steer, 0f),
            new Segment("parked: large right 120", 2.5f, Action.Steer, 120f),
            new Segment("parked: large left 160", 3.0f, Action.Steer, -40f),
            new Segment("parked: let go", 1.0f, Action.LetGo),
            new Segment("parked: double click, slow", 2.5f, Action.DoubleClick, -40f),
            new Segment("slow: small left 20", 2.0f, Action.Steer, -60f),
            new Segment("slow: small right 20", 2.0f, Action.Steer, -40f),
            new Segment("slow: large right 120", 3.0f, Action.Steer, 80f),
            new Segment("slow: large left 160", 3.5f, Action.Steer, -80f),
            new Segment("slow: let go", 1.0f, Action.LetGo),
            new Segment("slow: double click, cruise", 3.0f, Action.DoubleClick, -80f),
            new Segment("cruise: small right 20", 2.0f, Action.Steer, -60f),
            new Segment("cruise: small left 20", 2.0f, Action.Steer, -80f),
            new Segment("cruise: large right 120", 3.0f, Action.Steer, 40f),
            new Segment("cruise: large left 160", 3.5f, Action.Steer, -120f),
            new Segment("cruise: sweep right 15 (1/4)", 0.7f, Action.Steer, -105f),
            new Segment("cruise: sweep right 15 (2/4)", 0.7f, Action.Steer, -90f),
            new Segment("cruise: sweep right 15 (3/4)", 0.7f, Action.Steer, -75f),
            new Segment("cruise: sweep right 15 (4/4)", 0.7f, Action.Steer, -60f),
            new Segment("cruise: let go", 1.0f, Action.LetGo),
            new Segment("cruise: long press, sprint", 3.0f, Action.Hold, -60f),
            new Segment("sprint: large right 120 in the wave", 2.0f, Action.Steer, 60f),
            new Segment("sprint: let go, glide", 2.5f, Action.LetGo),
        };

        /// <summary>Long straight runs in each gear: a parked kick's glide, slow on the back, cruising, the sprint into the wave, its glide, the brake.</summary>
        public static List<Segment> SpeedScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: click ahead, kick and glide", 3.0f, Action.Click, 0f),
            new Segment("parked: double click, slow", 0.6f, Action.DoubleClick, 0f),
            new Segment("slow: on the back", 5.0f, Action.Idle),
            new Segment("slow: double click, cruise", 0.6f, Action.DoubleClick, 0f),
            new Segment("cruise: on the belly", 5.0f, Action.Idle),
            new Segment("cruise: long press, sprint into the wave", 6.0f, Action.Hold, 0f),
            new Segment("sprint: let go, slow out of the wave", 4.0f, Action.Idle),
            new Segment("cruise: right hold, brake", 2.0f, Action.RightHold),
            new Segment("parked: rest", 2.0f, Action.Idle),
        };

        public static List<Segment> Script(string name) => name switch
        {
            "turns" => TurnScript(),
            "speeds" => SpeedScript(),
            _ => BaselineScript(),
        };

        private void Start()
        {
            if (controls == null) controls = FindObjectOfType<OtterControls>();
            if (controls == null)
            {
                Debug.LogWarning("[OtterInputPlayback] No OtterControls found; playback disabled.", this);
                enabled = false;
                return;
            }
            if (segments.Count == 0) segments = BaselineScript();
            StartYaw = controls.transform.eulerAngles.y;
            pointed = Pointing(StartYaw, clickDistanceBL);
            previousCaptureFramerate = Time.captureFramerate;
            if (captureFramerate > 0) Time.captureFramerate = captureFramerate;
            controls.PointerOverride = this;
            startTime = Time.time;
        }

        private void Update()
        {
            if (Finished || controls == null) return;
            float t = Time.time - startTime;
            int index = 0;
            float end = 0f;
            for (; index < segments.Count; index++)
            {
                end += segments[index].duration;
                if (t < end) break;
            }
            if (index >= segments.Count)
            {
                Left = Right = false;
                Finished = true;
                return;
            }
            if (index != SegmentIndex)
            {
                SegmentIndex = index;
                SegmentStartTime = Time.time;
                pickUp = segments[index].action == Action.Steer && !controls.Gears.Selected;
            }

            var s = segments[index];
            float u = Time.time - SegmentStartTime;
            bool InTap(float from) => u >= from && u < from + tap;
            Left = s.action switch
            {
                Action.Click => InTap(lead),
                Action.DoubleClick => InTap(lead) || InTap(lead + tap + gap),
                Action.Hold => u >= lead,
                Action.LetGo => InTap(lead),
                _ => false,
            };
            Right = s.action switch
            {
                Action.RightTap => InTap(lead),
                Action.RightHold => u >= lead,
                Action.Steer => pickUp && InTap(lead),
                _ => false,
            };

            if (s.Points) pointed = Pointing(TargetYaw(index), s.distanceBL > 0f ? s.distanceBL : s.action == Action.Steer ? guideDistanceBL : clickDistanceBL);
            // picking it up and letting it go are clicks on the otter
            bool onOtter = (s.action == Action.Steer && Right) || (s.action == Action.LetGo && Left);
            ScreenPosition = ToScreen(onOtter ? controls.GrabPoint : controls.transform.position + pointed);
        }

        private Vector3 Pointing(float yaw, float distanceBL)
        {
            var brain = controls.brain;
            float length = brain != null && brain.BodyLength > 0f ? brain.BodyLength : 0.4f;
            return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (distanceBL * length);
        }

        private void OnDisable()
        {
            if (controls != null && ReferenceEquals(controls.PointerOverride, this)) controls.PointerOverride = null;
            Time.captureFramerate = previousCaptureFramerate;
        }

        private Vector2 ToScreen(Vector3 world)
        {
            var cam = controls.PointerCamera;
            world.y = controls.PointerY;
            return cam != null ? (Vector2)cam.WorldToScreenPoint(world) : Vector2.zero;
        }
    }
}
