using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion.Testing
{
    /// <summary>
    /// Scripted ring input for repeatable locomotion tests. Each segment holds the pointer in a ring zone toward a
    /// heading relative to the otter's facing when playback started (+ = clockwise seen from above, i.e. right).
    /// Feeds MovementControllerRB through its pointer override, so the controller's own smoothing, zones and
    /// release behaviour apply exactly as with the mouse. Locks the frame rate so runs repeat.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class OtterInputPlayback : MonoBehaviour, MovementControllerRB.IPointerSource
    {
        public enum Zone { Release, Aim, Swim, Sprint }

        [Serializable]
        public struct Segment
        {
            public string name;
            public float duration;
            public Zone zone;
            [Tooltip("Heading relative to the otter's facing at playback start (degrees, + = right).")]
            public float headingDeg;
            [Tooltip("Press the posture toggle as the segment starts.")]
            public bool pressFlip;
            [Tooltip("Arrow length as a fraction of the outer ring's radius; 0 = the middle of the zone.")]
            public float reach;

            public Segment(string name, float duration, Zone zone, float headingDeg, bool pressFlip = false, float reach = 0f)
            {
                this.name = name;
                this.duration = duration;
                this.zone = zone;
                this.headingDeg = headingDeg;
                this.pressFlip = pressFlip;
                this.reach = reach;
            }
        }

        public MovementControllerRB movement;
        [Tooltip("Empty = the built-in baseline script.")]
        public List<Segment> segments = new List<Segment>();
        [Tooltip("Fixed frames per second while playing back (Time.captureFramerate); 0 leaves the frame rate free.")]
        public int captureFramerate = 60;

        public bool Held { get; private set; }
        public Vector2 ScreenPosition { get; private set; }
        public bool Finished { get; private set; }
        public int SegmentIndex { get; private set; } = -1;
        public float SegmentStartTime { get; private set; }
        public float StartYaw { get; private set; }
        public Segment CurrentSegment => SegmentIndex >= 0 && SegmentIndex < segments.Count ? segments[SegmentIndex] : default;
        public float TargetYaw(int index) => StartYaw + segments[index].headingDeg;

        private float startTime;
        private int previousCaptureFramerate;

        /// <summary>Straight runs, swim and sprint turns of 90 and 170 degrees, a coast to stop and an in-place pivot.</summary>
        public static List<Segment> BaselineScript() => new List<Segment>
        {
            new Segment("idle", 1.0f, Zone.Release, 0f),
            new Segment("swim straight", 2.5f, Zone.Swim, 0f),
            new Segment("swim turn left 90", 3.0f, Zone.Swim, -90f),
            new Segment("swim turn right 170", 3.5f, Zone.Swim, 80f),
            new Segment("sprint straight", 2.5f, Zone.Sprint, 80f),
            new Segment("sprint turn left 80", 3.0f, Zone.Sprint, 0f),
            new Segment("release, coast to stop", 2.5f, Zone.Release, 0f),
            new Segment("aim pivot right 90", 2.5f, Zone.Aim, 90f),
            new Segment("release", 1.5f, Zone.Release, 90f),
        };

        /// <summary>
        /// Small (20°) and large (120° / 160°) turns each way inside the inner ring (in place), between the rings
        /// (swimming, on the back) and outside the outer ring (sprinting, prone), plus a gentle sweep in 15° steps.
        /// </summary>
        public static List<Segment> TurnScript() => new List<Segment>
        {
            new Segment("rest", 1.5f, Zone.Release, 0f),
            new Segment("in ring: small left 20", 1.5f, Zone.Aim, -20f),
            new Segment("in ring: small right 20", 1.5f, Zone.Aim, 0f),
            new Segment("in ring: large right 120", 2.5f, Zone.Aim, 120f),
            new Segment("in ring: large left 160", 3.0f, Zone.Aim, -40f),
            new Segment("release", 1.0f, Zone.Release, -40f),
            new Segment("swim: straight", 2.5f, Zone.Swim, -40f),
            new Segment("swim: small left 20", 2.0f, Zone.Swim, -60f),
            new Segment("swim: small right 20", 2.0f, Zone.Swim, -40f),
            new Segment("swim: large right 120", 3.0f, Zone.Swim, 80f),
            new Segment("swim: large left 160", 3.5f, Zone.Swim, -80f),
            new Segment("swim: sweep right 15 (1/4)", 0.7f, Zone.Swim, -65f),
            new Segment("swim: sweep right 15 (2/4)", 0.7f, Zone.Swim, -50f),
            new Segment("swim: sweep right 15 (3/4)", 0.7f, Zone.Swim, -35f),
            new Segment("swim: sweep right 15 (4/4)", 0.7f, Zone.Swim, -20f),
            new Segment("sprint: straight", 2.5f, Zone.Sprint, -20f),
            new Segment("sprint: small left 20", 2.0f, Zone.Sprint, -40f),
            new Segment("sprint: small right 20", 2.0f, Zone.Sprint, -20f),
            new Segment("sprint: large right 120", 3.0f, Zone.Sprint, 100f),
            new Segment("sprint: large left 160", 3.5f, Zone.Sprint, -60f),
            new Segment("release, coast", 2.5f, Zone.Release, -60f),
        };

        /// <summary>Long straight runs at each speed: on the back, the sprint's build-up and roll, prone sprint, prone cruise, then the glide to a stop.</summary>
        public static List<Segment> SpeedScript() => new List<Segment>
        {
            new Segment("idle", 1.0f, Zone.Release, 0f),
            new Segment("swim on the back", 5.0f, Zone.Swim, 0f),
            new Segment("sprint: build up on the back, roll, sprint prone", 6.0f, Zone.Sprint, 0f),
            new Segment("back into the swim ring: prone cruise", 5.0f, Zone.Swim, 0f),
            new Segment("release: glide, stop, supine", 4.0f, Zone.Release, 0f),
        };

        public static List<Segment> Script(string name) => name switch
        {
            "posture" => PostureScript(),
            "turns" => TurnScript(),
            "speeds" => SpeedScript(),
            _ => BaselineScript(),
        };

        /// <summary>Exercises every posture rule: the sprint build-up, prone held through pulling back and turning until it stops, manual toggles while swimming and at rest.</summary>
        public static List<Segment> PostureScript() => new List<Segment>
        {
            new Segment("rest (expect supine)", 1.5f, Zone.Release, 0f),
            new Segment("swim mid-ring: stays supine, eases to cap", 3.0f, Zone.Swim, 0f),
            new Segment("arrow out to 98%, still between the rings: stays supine", 2.0f, Zone.Swim, 0f, reach: 0.98f),
            new Segment("sprint: speeds up on its back, then prone", 2.5f, Zone.Sprint, 0f),
            new Segment("arrow back into the swim ring: stays prone", 2.5f, Zone.Swim, 0f),
            new Segment("prone swim turn right 120: stays prone", 3.0f, Zone.Swim, 120f),
            new Segment("aim in the inner ring: stops, then supine", 3.5f, Zone.Aim, 120f),
            new Segment("F while swimming -> prone, held", 2.5f, Zone.Swim, 120f, pressFlip: true),
            new Segment("release: glide, then supine", 3.0f, Zone.Release, 120f),
            new Segment("F at rest -> prone", 1.5f, Zone.Release, 120f, pressFlip: true),
            new Segment("aim pivot left 90: stays prone (it has not swum)", 2.5f, Zone.Aim, 30f),
            new Segment("sprint: already prone", 2.0f, Zone.Sprint, 30f),
            new Segment("sprint turn left 120: stays prone", 3.0f, Zone.Sprint, -90f),
            new Segment("release: stops, then supine", 2.5f, Zone.Release, -90f),
        };

        private void Start()
        {
            if (movement == null) movement = FindObjectOfType<MovementControllerRB>();
            if (movement == null)
            {
                Debug.LogWarning("[OtterInputPlayback] No MovementControllerRB found; playback disabled.", this);
                enabled = false;
                return;
            }
            if (segments.Count == 0) segments = BaselineScript();
            StartYaw = movement.transform.eulerAngles.y;
            previousCaptureFramerate = Time.captureFramerate;
            if (captureFramerate > 0) Time.captureFramerate = captureFramerate;
            movement.PointerOverride = this;
            startTime = Time.time;
        }

        private void Update()
        {
            if (Finished || movement == null) return;
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
                Held = false;
                Finished = true;
                return;
            }
            if (index != SegmentIndex)
            {
                SegmentIndex = index;
                SegmentStartTime = Time.time;
                if (segments[index].pressFlip)
                {
                    var brain = movement.GetComponent<OtterLocomotionBrain>();
                    if (brain != null) brain.RequestPostureToggle();
                }
            }
            var s = segments[index];
            Held = s.zone != Zone.Release;
            if (Held) ScreenPosition = ToScreen(TargetYaw(index), s.zone, s.reach);
        }

        private void OnDisable()
        {
            if (movement != null && ReferenceEquals(movement.PointerOverride, this)) movement.PointerOverride = null;
            Time.captureFramerate = previousCaptureFramerate;
        }

        // Inverse of MovementControllerRB.ScreenDirToWorldXZ: the screen direction whose flattened camera-space image
        // points along the world yaw, at the requested reach or else the middle of the requested ring zone.
        private Vector2 ToScreen(float yawDeg, Zone zone, float reach)
        {
            var ui = movement.RingUI;
            var cam = movement.InputCamera != null ? movement.InputCamera : Camera.main;
            Vector3 w = Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward;
            Vector2 dir = Vector2.up;
            if (cam != null)
            {
                Vector3 r = cam.transform.right, u = cam.transform.up;
                float det = r.x * u.z - u.x * r.z;
                if (Mathf.Abs(det) > 1e-6f)
                    dir = new Vector2((w.x * u.z - u.x * w.z) / det, (r.x * w.z - w.x * r.z) / det).normalized;
            }
            float inner = ui != null ? ui.InnerRadiusPx : 120f;
            float outer = ui != null ? ui.OuterRadiusPx : 260f;
            float radius = reach > 0f ? outer * reach : zone switch
            {
                Zone.Aim => inner * 0.5f,
                Zone.Swim => (inner + outer) * 0.5f,
                Zone.Sprint => outer * 1.25f,
                _ => 0f,
            };
            Vector2 center = ui != null ? ui.ScreenCenterPx : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            return center + dir * radius;
        }
    }
}
