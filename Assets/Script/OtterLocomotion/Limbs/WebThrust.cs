using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// The hind webs push the body through the water, so its speed comes from the kicks. Each web is a flat plate
    /// (its face and size from the skinned mesh, OtterRig.TryFootPlane, spread or folded by LimbStrokeAnimator):
    /// moving through still water it takes a push back along its face. Face-on that goes by the square of its speed;
    /// slicing through at a slant a short, broad plate like a web is pushed across its path much harder than its
    /// face-on share alone, as a hydrofoil is, so the fluke's pitched beat drives as the paddling does. A web
    /// sweeping back faster than the body moves forward drives the body on; one dragged forward, a recovery not
    /// feathered edge-on or a foot left trailing, holds it back. Only the push along the body's heading is kept;
    /// OtterControls turns the body.
    /// </summary>
    [DefaultExecutionOrder(380)]
    [DisallowMultipleComponent]
    public class WebThrust : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public LimbStrokeAnimator animator;
        [Tooltip("Water's push on a web per square metre at 1 m/s face-on (N): half the water's density times a flat plate's drag coefficient is about 600.")]
        public float waterPush = 600f;
        [Tooltip("The push across its path on a web slicing through at a slant, per square metre, per radian of slant, at 1 m/s (N): half the water's density times about pi/2 for a plate about as long as it is wide.")]
        public float slicePush = 800f;
        [Tooltip("Web area as a multiple of the one measured from the mesh.")]
        public float areaScale = 1.4f;
        [Tooltip("Fastest a web is taken to move (m/s), against jumps in its pose.")]
        public float maxWebSpeed = 4f;
        [Tooltip("Count the body's swing about its centre in a turn as web motion through the water. OtterControls " +
                 "turns the body rigidly, sweeping the hind feet sideways far faster than a real otter's, whose hindquarters " +
                 "follow its path through the bend; a web slanted to that sweep is pushed back and the otter stalls in every " +
                 "turn. Off, the webs move through the water with the body's travel and their own strokes.")]
        public bool sweptByTurn = false;

        private class Web
        {
            public LimbId limb;
            public Transform bone;
            public Vector3 normalLocal, centerLocal, previous;
            /// <summary>The web's skin, in the bone's space.</summary>
            public Vector3[] points;
            public float area;
            public bool hasPrevious;
            public float push, wet;
        }

        private readonly Web[] webs = new Web[2];
        private Water water;
        private Rigidbody body;
        private Vector3 impulse, force;
        private float time;
        private bool started;
        private Vector3 previousPivot, previousHeading;

        /// <summary>A hind web's push on the body along its heading (N, + = forward) last frame; 0 without one.</summary>
        public float Push(LimbId limb)
        {
            foreach (var w in webs)
                if (w != null && w.limb == limb) return w.push;
            return 0f;
        }

        /// <summary>A hind web's area as measured, before areaScale and its spread (m²); 0 without one.</summary>
        public float Area(LimbId limb)
        {
            foreach (var w in webs)
                if (w != null && w.limb == limb) return w.area;
            return 0f;
        }

        /// <summary>The share of a hind web under the water's surface last frame: 1 all in, 0 all out, and 1 with no Water in the scene.</summary>
        public float Immersion(LimbId limb)
        {
            foreach (var w in webs)
                if (w != null && w.limb == limb) return w.wet;
            return 0f;
        }

        public bool HasWebs => webs[0] != null || webs[1] != null;

        private void Awake()
        {
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
        }

        // on Start: when added while the scene loads, the IK chains are not enabled yet
        private void Start()
        {
            started = true;
            Setup();
        }

        private void OnEnable()
        {
            if (started) Setup();
        }

        private void OnDisable()
        {
            impulse = force = Vector3.zero;
            time = 0f;
        }

        private void Setup()
        {
            body = GetComponent<Rigidbody>();
            water = FindObjectOfType<Water>();
            var chains = OtterRig.FindLimbChains(transform);
            int k = 0;
            foreach (var limb in new[] { LimbId.HL, LimbId.HR })
            {
                webs[k] = null;
                var ik = chains[(int)limb];
                if (ik != null && ik.end.bone != null)
                {
                    var bone = OtterRig.Flipper(ik.end.bone);
                    if (OtterRig.TryFootPlane(bone, out var normal, out _, out var center, out var extents))
                    {
                        var skin = OtterRig.FollowingVertices(bone);
                        // a flat spread of points: its width is about sqrt(12) standard deviations each way
                        webs[k] = new Web
                        {
                            limb = limb, bone = bone, normalLocal = normal, centerLocal = center, area = 12f * extents.y * extents.z,
                            points = skin != null ? skin.Select(bone.InverseTransformPoint).ToArray() : new[] { center },
                        };
                    }
                }
                k++;
            }
            if (!HasWebs) Debug.LogWarning($"[WebThrust] No hind webs found under '{name}'.");
        }

        // after the limb IK and the flippers' turn (AfterLimbIK, 370)
        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (body == null || dt <= 0f) return;
            Vector3 heading = Vector3.ProjectOnPlane(body.rotation * Vector3.forward, Vector3.up).normalized;
            var root = body.transform;
            Vector3 pivot = root.position, facing = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
            // where each web would be had the body only travelled since last frame, not swung round
            Quaternion swing = sweptByTurn || previousHeading == Vector3.zero ? Quaternion.identity : Quaternion.FromToRotation(previousHeading, facing);
            Vector3 total = Vector3.zero;
            foreach (var w in webs)
            {
                if (w == null || w.bone == null) continue;
                w.wet = Wet(w);
                Vector3 p = w.bone.TransformPoint(w.centerLocal);
                Vector3 n = w.bone.TransformDirection(w.normalLocal).normalized;
                Vector3 from = previousPivot + swing * (w.previous - previousPivot);
                Vector3 v = w.hasPrevious ? Vector3.ClampMagnitude((p - from) / dt, maxWebSpeed) : Vector3.zero;
                w.previous = p;
                w.hasPrevious = true;
                float spread = animator != null && animator.isActiveAndEnabled ? Mathf.Max(0f, animator.FlipperState(w.limb).z) : 1f;
                if (spread <= 0f) spread = 1f;
                float face = Vector3.Dot(v, n);
                float along = (v - face * n).magnitude;
                // the water pushes back against the face's motion: |v|^2 (slicePush sin cos + waterPush sin^2) of its slant
                Vector3 f = -(waterPush * Mathf.Abs(face) + slicePush * along) * w.area * areaScale * spread * face * n;
                w.push = Vector3.Dot(f, heading);
                total += heading * w.push;
            }
            previousPivot = pivot;
            previousHeading = facing;
            impulse += total * dt;
            time += dt;
        }

        // the share of the web's skin below the surface
        private float Wet(Web w)
        {
            if (water == null) return 1f;
            int under = 0;
            foreach (var p in w.points)
                if (water.Depth(w.bone.TransformPoint(p)) > 0f) under++;
            return under / (float)w.points.Length;
        }

        private void FixedUpdate()
        {
            if (body == null) return;
            // the frames' pushes since the last physics step, held when no frame came between
            if (time > 0f) force = impulse / time;
            impulse = Vector3.zero;
            time = 0f;
            body.AddForce(force, ForceMode.Force);
        }
    }
}
