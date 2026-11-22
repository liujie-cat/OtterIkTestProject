using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion.Testing
{
    /// <summary>
    /// Scripted mouse and key input for repeatable locomotion tests. Each segment does one thing, as the player would:
    /// a click on the water, a press or hold of the go key, steering the otter picked up (the go key, then the pointer
    /// as its guide), a press or hold of the back key or the dive key; pointing toward a yaw relative to the otter's facing when
    /// playback started (+ = clockwise seen from above, i.e. right), held there or swept round the otter from it.
    /// Between pointing segments the pointer stays where it last pointed from the otter, as a still mouse does under
    /// the following camera. Feeds OtterControls through its input override, so its gestures and gears apply exactly
    /// as with the mouse and keys. Locks the frame rate so runs repeat.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class OtterInputPlayback : MonoBehaviour, OtterControls.IInput
    {
        /// <summary>Go and Steer pick the otter up first with a press of the go key if it is not picked up yet.</summary>
        public enum Action { Idle, Click, Go, GoHold, Steer, Back, BackHold, Dive, DiveHold }

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
            [Tooltip("How fast the pointer sweeps round the otter from there (deg/s, + = right), as a mouse dragged round it; 0 = held still.")]
            public float sweepDegPerSec;

            public Segment(string name, float duration, Action action, float headingDeg = 0f, float distanceBL = 0f, float sweepDegPerSec = 0f)
            {
                this.name = name;
                this.duration = duration;
                this.action = action;
                this.headingDeg = headingDeg;
                this.distanceBL = distanceBL;
                this.sweepDegPerSec = sweepDegPerSec;
            }

            public bool Points => action == Action.Click || action == Action.Go || action == Action.GoHold || action == Action.Steer;
        }

        public OtterControls controls;
        [Tooltip("Empty = the built-in baseline script.")]
        public List<Segment> segments = new List<Segment>();
        [Tooltip("Fixed frames per second while playing back (Time.captureFramerate); 0 leaves the frame rate free.")]
        public int captureFramerate = 60;
        [Tooltip("Button and keys up at the start of each segment (s), so its press is a new one; steering it while it is picked up already doesn't pick it up again.")]
        public float lead = 0.05f;
        [Tooltip("How long a click or a key press holds it down, and the gap between picking the otter up and the press that shifts (s).")]
        public float tap = 0.08f;
        public float gap = 0.1f;
        [Tooltip("Usual distance from the otter (body lengths) of a click or a go press, and of the guide point while steering.")]
        public float clickDistanceBL = 2f;
        public float guideDistanceBL = 1.5f;

        public bool Click { get; private set; }
        public bool Go { get; private set; }
        public bool Back { get; private set; }
        public bool Dive { get; private set; }
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
        private bool pickUp, holdOn;

        /// <summary>
        /// Every gear and gesture of the controls once: parked clicks, picking it up and steering it, the go key up to
        /// slow and to cruising, turns and clicks in each, the held go key's sprint into the wave, a jump, the glide out
        /// of it, the back key down, and held, the brake to a stop that lets it go.
        /// </summary>
        public static List<Segment> BaselineScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: click ahead, one kick", 2.0f, Action.Click, 0f),
            new Segment("parked: click right 90, turn and kick", 2.5f, Action.Click, 90f),
            new Segment("parked: pick it up, steer left 90, small arc", 2.5f, Action.Steer, 0f),
            new Segment("parked: go, slow", 3.0f, Action.Go, 0f),
            new Segment("slow: straight on the back", 2.5f, Action.Idle),
            new Segment("slow: steer right 90", 3.0f, Action.Steer, 90f),
            new Segment("slow: click left 45, kick", 2.0f, Action.Click, 45f),
            new Segment("slow: go, cruise", 3.0f, Action.Go, 45f),
            new Segment("cruise: straight on the belly", 2.5f, Action.Idle),
            new Segment("cruise: steer left 90", 3.0f, Action.Steer, -45f),
            new Segment("cruise: hold go, sprint", 3.5f, Action.GoHold, -45f),
            new Segment("sprint: let go in the wave", 0.3f, Action.Idle),
            new Segment("sprint: go, jump", 1.2f, Action.Go, -45f),
            new Segment("sprint: glide out of the wave", 3.0f, Action.Idle),
            new Segment("cruise: back, slow", 3.0f, Action.Back),
            new Segment("slow: hold back, brake and let go of it", 2.0f, Action.BackHold),
            new Segment("parked: rest", 1.5f, Action.Idle),
        };

        /// <summary>Small (20°) and large (120° / 160°) turns each way in every gear, plus a gentle sweep in 15° steps cruising.</summary>
        public static List<Segment> TurnScript() => new List<Segment>
        {
            new Segment("parked: rest", 1.0f, Action.Idle),
            new Segment("parked: small left 20", 1.5f, Action.Steer, -20f),
            new Segment("parked: small right 20", 1.5f, Action.Steer, 0f),
            new Segment("parked: large right 120", 2.5f, Action.Steer, 120f),
            new Segment("parked: large left 160", 3.0f, Action.Steer, -40f),
            new Segment("parked: let go", 1.0f, Action.Back),
            new Segment("parked: pick it up, go, slow", 2.5f, Action.Go, -40f),
            new Segment("slow: small left 20", 2.0f, Action.Steer, -60f),
            new Segment("slow: small right 20", 2.0f, Action.Steer, -40f),
            new Segment("slow: large right 120", 3.0f, Action.Steer, 80f),
            new Segment("slow: large left 160", 3.5f, Action.Steer, -80f),
            new Segment("slow: hold the heading", 1.0f, Action.Idle),
            new Segment("slow: go, cruise", 3.0f, Action.Go, -80f),
            new Segment("cruise: small right 20", 2.0f, Action.Steer, -60f),
            new Segment("cruise: small left 20", 2.0f, Action.Steer, -80f),
            new Segment("cruise: large right 120", 3.0f, Action.Steer, 40f),
            new Segment("cruise: large left 160", 3.5f, Action.Steer, -120f),
            new Segment("cruise: sweep right 15 (1/4)", 0.7f, Action.Steer, -105f),
            new Segment("cruise: sweep right 15 (2/4)", 0.7f, Action.Steer, -90f),
            new Segment("cruise: sweep right 15 (3/4)", 0.7f, Action.Steer, -75f),
            new Segment("cruise: sweep right 15 (4/4)", 0.7f, Action.Steer, -60f),
            new Segment("cruise: hold the heading", 1.0f, Action.Idle),
            new Segment("cruise: hold go, sprint", 3.0f, Action.GoHold, -60f),
            new Segment("sprint: large right 120 in the wave", 2.0f, Action.Steer, 60f),
            new Segment("sprint: glide", 2.5f, Action.Idle),
        };

        /// <summary>Long straight runs in each gear: a parked kick's glide, slow on the back, cruising, the sprint into the wave, the back key down out of it to cruising and on to slow on the back, the brake.</summary>
        public static List<Segment> SpeedScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: click ahead, kick and glide", 3.0f, Action.Click, 0f),
            new Segment("parked: pick it up, go, slow", 0.6f, Action.Go, 0f),
            new Segment("slow: on the back", 5.0f, Action.Idle),
            new Segment("slow: go, cruise", 0.6f, Action.Go, 0f),
            new Segment("cruise: on the belly", 5.0f, Action.Idle),
            new Segment("cruise: hold go, sprint into the wave", 6.0f, Action.GoHold, 0f),
            new Segment("sprint: back, cruise", 1.0f, Action.Back),
            new Segment("cruise: back, slow on the back", 5.0f, Action.Back),
            new Segment("slow: hold back, brake and let go of it", 2.0f, Action.BackHold),
            new Segment("parked: rest", 2.0f, Action.Idle),
        };

        /// <summary>
        /// Long turns with the pointer swept round the otter, as a mouse dragged round it, so the body keeps turning and
        /// leaning through many kicks: on the back, on the belly gently and tightly, and sprinting in the wave, each kind
        /// after a straight stretch to set it against.
        /// </summary>
        public static List<Segment> CircleScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: pick it up, go, slow", 2.0f, Action.Go, 0f),
            new Segment("slow: straight on the back", 2.5f, Action.Idle),
            new Segment("slow: steer round to the right", 6.0f, Action.Steer, 20f, 0f, 60f),
            new Segment("slow: go, cruise", 2.5f, Action.Go, 20f),
            new Segment("cruise: straight on the belly", 2.5f, Action.Idle),
            new Segment("cruise: steer round to the left", 6.0f, Action.Steer, 0f, 0f, -60f),
            new Segment("cruise: steer round tight to the right", 4.0f, Action.Steer, 30f, 0f, 120f),
            new Segment("cruise: hold go, sprint", 3.5f, Action.GoHold, 150f),
            new Segment("sprint: sweep round to the left in the wave", 4.0f, Action.GoHold, 150f, 0f, -60f),
            new Segment("sprint: let go, glide", 2.0f, Action.Idle),
            new Segment("cruise: back, slow", 3.0f, Action.Back),
            new Segment("slow: hold back, brake and let go of it", 2.0f, Action.BackHold),
            new Segment("parked: rest", 1.5f, Action.Idle),
        };

        /// <summary>
        /// The dolphin wave alone: the held go key's sprint into it, a long straight run in it, a gentle sweep it keeps
        /// the wave through, a large turn it takes on its belly with all four limbs before going back into the wave on
        /// the new heading, and the glide out of it.
        /// </summary>
        public static List<Segment> DolphinScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: pick it up, go, slow", 1.5f, Action.Go, 0f),
            new Segment("slow: go, cruise", 2.0f, Action.Go, 0f),
            new Segment("cruise: hold go, sprint into the wave", 3.5f, Action.GoHold, 0f),
            new Segment("sprint: straight in the wave", 4.0f, Action.GoHold, 0f),
            new Segment("sprint: sweep gently round to the right in the wave", 4.0f, Action.GoHold, 0f, 0f, 30f),
            new Segment("sprint: large left 140, on the belly", 4.0f, Action.GoHold, -20f),
            new Segment("sprint: straight on in the wave", 3.0f, Action.GoHold, -20f),
            new Segment("sprint: let go, glide", 2.5f, Action.Idle),
        };

        /// <summary>
        /// Diving: a duck dive from cruising, on under water on the belly, the dive key held deeper and let go at that
        /// depth, a turn and the sprint into the wave down there, a tap back up to cruising at the surface; then down
        /// from slow on the back, rolling over into the dive and cruising on once under, and back up, cruising still.
        /// </summary>
        public static List<Segment> DiveScript() => new List<Segment>
        {
            new Segment("parked: idle", 1.0f, Action.Idle),
            new Segment("parked: pick it up, go, slow", 1.5f, Action.Go, 0f),
            new Segment("slow: go, cruise", 2.5f, Action.Go, 0f),
            new Segment("cruise: dive, a duck dive", 3.0f, Action.Dive),
            new Segment("under: cruise on the belly", 2.5f, Action.Idle),
            new Segment("under: hold dive, deeper", 3.0f, Action.DiveHold),
            new Segment("under: let go at that depth, steer right 90", 3.0f, Action.Steer, 90f),
            new Segment("under: hold go, sprint into the wave", 4.0f, Action.GoHold, 90f),
            new Segment("under: let go, glide", 2.0f, Action.Idle),
            new Segment("under: dive, back up", 4.0f, Action.Dive),
            new Segment("cruise: on the belly at the surface", 2.0f, Action.Idle),
            new Segment("cruise: back, slow on the back", 2.0f, Action.Back),
            new Segment("slow: dive from the back, rolled over it cruises", 3.0f, Action.Dive),
            new Segment("under: cruise on the belly", 2.0f, Action.Idle),
            new Segment("under: dive, back up", 4.0f, Action.Dive),
            new Segment("cruise: on the belly at the surface", 1.5f, Action.Idle),
        };

        public static List<Segment> Script(string name) => name switch
        {
            "turns" => TurnScript(),
            "speeds" => SpeedScript(),
            "circles" => CircleScript(),
            "dolphin" => DolphinScript(),
            "dive" => DiveScript(),
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
            controls.InputOverride = this;
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
                Click = Go = Back = Dive = false;
                Finished = true;
                return;
            }
            if (index != SegmentIndex)
            {
                SegmentIndex = index;
                SegmentStartTime = Time.time;
                var action = segments[index].action;
                pickUp = (action == Action.Steer || action == Action.Go) && !controls.Gears.Selected;
                // a hold going on from the segment before is not let go between them: a fresh go press in the wave jumps
                holdOn = index > 0 && segments[index - 1].action == action && (action == Action.GoHold || action == Action.BackHold || action == Action.DiveHold);
            }

            var s = segments[index];
            float u = Time.time - SegmentStartTime;
            bool InTap(float from) => u >= from && u < from + tap;
            Click = s.action == Action.Click && InTap(lead);
            Go = s.action switch
            {
                Action.Go => InTap(lead) || pickUp && InTap(lead + tap + gap),
                Action.GoHold => holdOn || u >= lead,
                Action.Steer => pickUp && InTap(lead),
                _ => false,
            };
            Back = s.action switch
            {
                Action.Back => InTap(lead),
                Action.BackHold => holdOn || u >= lead,
                _ => false,
            };
            Dive = s.action switch
            {
                Action.Dive => InTap(lead),
                Action.DiveHold => holdOn || u >= lead,
                _ => false,
            };

            if (s.Points) pointed = Pointing(TargetYaw(index) + s.sweepDegPerSec * u, s.distanceBL > 0f ? s.distanceBL : s.action == Action.Steer ? guideDistanceBL : clickDistanceBL);
            ScreenPosition = ToScreen(controls.transform.position + pointed);
        }

        private Vector3 Pointing(float yaw, float distanceBL)
        {
            var brain = controls.brain;
            float length = brain != null && brain.BodyLength > 0f ? brain.BodyLength : 0.4f;
            return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * (distanceBL * length);
        }

        private void OnDisable()
        {
            if (controls != null && ReferenceEquals(controls.InputOverride, this)) controls.InputOverride = null;
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
