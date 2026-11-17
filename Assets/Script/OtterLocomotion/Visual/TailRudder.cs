using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Drives the tail bones (OtterRig.TailRoot and the tail bones below it). Each joint is a point dragged through still
    /// water behind the hips and held to the tail's shape by a spring that softens toward the tip, so the tail trails
    /// the path, lags the hips' swings and whips back after them, and the spine's fluke wave runs on down it. As a
    /// rudder it bends toward the side the otter turns to, following the brain's turn rate so it leads the body, and it
    /// swings away from each hind foot as that foot pushes, with the curl of the body (HindBend). It is posed after the
    /// legs and keeps out of them (HindLegColliders): a shin or web that sweeps into it carries it aside, without
    /// flinging it.
    /// </summary>
    [DefaultExecutionOrder(375)]
    [DisallowMultipleComponent]
    public class TailRudder : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("Found on this object when empty; without one the tail passes through the legs.")]
        public HindLegColliders legs;
        [Tooltip("Keep out of the hind legs.")]
        public bool collide = true;
        [Tooltip("Scale on the tail's thickness measured from the mesh.")]
        public float thickness = 1f;
        [Tooltip("Fastest a web pushes the tail aside (m/s); the shins push it out at once. The webs are thin and give, so one turning over fast against the tail only brushes it, while one pressed on it pushes it clear.")]
        public float webPushSpeed = 1f;
        [Tooltip("How long the tip takes to follow the hips (s); joints nearer the root follow sooner.")]
        [Range(0.005f, 0.3f)] public float lag = 0.07f;
        [Tooltip("Damping of each joint's spring (1 = no overshoot; lower = a livelier whip after the hips stop).")]
        [Range(0.2f, 1.5f)] public float damping = 0.55f;
        [Tooltip("Most a joint may bend from the one before it (deg).")]
        public float maxBend = 30f;

        [Header("Rudder")]
        [Tooltip("Bend toward the side turned to at a full turn, over the whole tail (deg; negative bends it away).")]
        public float rudder = 45f;
        [Tooltip("Turn rate that counts as a full turn (deg/s).")]
        public float fullTurnRate = 180f;
        [Tooltip("How quickly the rudder follows the turn (s).")]
        public float rudderTime = 0.12f;
        [Tooltip("Sway with the hind pushes, over the whole tail (deg): each push curls the body away from the pushing foot (HindBend), and the tail tip goes with the curl.")]
        public float kickSway = 22f;
        [Tooltip("Yaw moment of a push (N·m) that sways the tail most of the way.")]
        public float fullMoment = 0.05f;

        private readonly List<Transform> bones = new List<Transform>();
        // the body's dorsal side in the space of the bone the tail hangs from
        private Vector3 upLocal;
        private Quaternion[] rest;
        private Vector3 tipLocal, straightTip;
        // points 0..n-1 at the bones, n past the last bone's end; point 0 rides on the hips. free: where they would be
        // this step without the legs' shoves
        private Vector3[] point, free, velocity, goal, lastGoal;
        // per bone, from it to the next point
        private float[] length, radius;
        private float turn;
        private bool settled;
        private const int BallsPerBone = 4;

        /// <summary>The tail's bend from the hips' line at the tip (deg, + = to the otter's right in the world, seen from above).</summary>
        public float TipBend { get; private set; }
        /// <summary>The tip's lift above the hips' line (deg, + = toward the back).</summary>
        public float TipLift { get; private set; }
        /// <summary>The rudder's share of the bend asked for (-1..1 of a full turn, + = right).</summary>
        public float Rudder => turn;
        /// <summary>How deep the tail sits in the hind legs as posed (m), NaN without HindLegColliders.</summary>
        public float LegOverlap { get; private set; } = float.NaN;
        /// <summary>The tail's thickness measured from the mesh (cm, root to tip), for the logs.</summary>
        public string Describe() => radius == null ? "-" : string.Join("/", radius.Select(r => (r * thickness * 100f).ToString("F1")));

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (legs == null) legs = GetComponent<HindLegColliders>();
        }

        // after SpineDecouplerWithShadows has rebuilt the spine in Awake
        private void Start()
        {
            var bone = OtterRig.FindActiveBone(transform, OtterRig.TailRoot);
            while (bone != null && bones.Count < 12)
            {
                bones.Add(bone);
                Transform next = null;
                foreach (Transform child in bone)
                    if (child.name.StartsWith("c_tail_") && child.gameObject.activeInHierarchy) { next = child; break; }
                bone = next;
            }
            if (bones.Count < 2)
            {
                Debug.LogWarning($"[TailRudder] No tail chain under '{name}' from '{OtterRig.TailRoot}'.");
                enabled = false;
                return;
            }
            int n = bones.Count;
            rest = new Quaternion[n];
            for (int j = 0; j < n; j++) rest[j] = bones[j].localRotation;
            upLocal = bones[0].parent.InverseTransformDirection(transform.up);
            Transform last = bones[n - 1];
            tipLocal = last.InverseTransformPoint(last.position + 0.8f * (last.position - bones[n - 2].position));
            point = new Vector3[n + 1];
            free = new Vector3[n + 1];
            velocity = new Vector3[n + 1];
            goal = new Vector3[n + 1];
            lastGoal = new Vector3[n + 1];
            length = new float[n];
            radius = new float[n];
            Shape(0f);
            for (int j = 0; j < n; j++)
            {
                length[j] = Vector3.Distance(goal[j], goal[j + 1]);
                radius[j] = OtterRig.TryRadius(bones[j], goal[j], goal[j + 1], out float r) ? r : j > 0 ? radius[j - 1] : 0.01f;
            }
            settled = false;
        }

        private void OnDisable()
        {
            if (rest == null) return;
            for (int j = 0; j < bones.Count; j++)
                if (bones[j] != null) bones[j].localRotation = rest[j];
            settled = false;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (rest == null || dt <= 0f) return;
            int n = bones.Count;

            float turnGoal = brain != null ? Mathf.Clamp(brain.Demand.yawRateTarget / Mathf.Max(1f, fullTurnRate), -1f, 1f) : 0f;
            turn = Mathf.Lerp(turn, turnGoal, 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, rudderTime)));
            // turning about world up by a negative angle swings what trails behind to the right
            Shape(-(rudder * turn + kickSway * HindBend.Now(brain, fullMoment)) / n);

            if (!settled || (goal[n] - point[n]).sqrMagnitude > 1f)
            {
                for (int j = 0; j <= n; j++)
                {
                    point[j] = lastGoal[j] = goal[j];
                    velocity[j] = Vector3.zero;
                }
                settled = true;
            }

            bool colliding = collide && legs != null && legs.Ready;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 16);
            float h = dt / steps;
            float webStep = Mathf.Max(0f, webPushSpeed) * h;
            for (int s = 1; s <= steps; s++)
            {
                float a = (float)s / steps;
                point[0] = free[0] = Vector3.Lerp(lastGoal[0], goal[0], a);
                Vector3 rootAlong = Vector3.Lerp(lastGoal[1], goal[1], a) - point[0];
                for (int j = 1; j <= n; j++)
                {
                    // a spring to the shape, damped against the still water; the tip's lags most
                    float tau = lag * j / n;
                    float w = 2f * damping / tau;
                    Vector3 target = Vector3.Lerp(lastGoal[j], goal[j], a);
                    Vector3 before = point[j];
                    velocity[j] += (w * w * (target - point[j]) - 2f * damping * w * velocity[j]) * h;
                    Vector3 moved = point[j] + velocity[j] * h;

                    // the legs move once a frame, so a contact shoves the tail a frame's worth in one step: the shove
                    // and what it drags along are kept out of the velocity, or the tail would be flung off the leg
                    free[j] = Hang(j, moved, free[j - 1], j == 1 ? rootAlong : free[j - 1] - free[j - 2]);
                    velocity[j] = (free[j] - before) / h;
                    point[j] = Hang(j, moved, point[j - 1], j == 1 ? rootAlong : point[j - 1] - point[j - 2]);
                    if (colliding)
                    {
                        Vector3 unpushed = point[j];
                        Collide(j, webStep);
                        Vector3 shove = point[j] - unpushed;
                        if (shove.sqrMagnitude > 1e-12f)
                        {
                            Vector3 normal = shove.normalized;
                            velocity[j] -= normal * Mathf.Min(0f, Vector3.Dot(velocity[j], normal));
                        }
                    }
                }
            }
            for (int j = 0; j <= n; j++) lastGoal[j] = goal[j];

            for (int j = 0; j < n; j++)
            {
                Vector3 end = j + 1 < n ? bones[j + 1].position : bones[n - 1].TransformPoint(tipLocal);
                Vector3 from = end - bones[j].position, to = point[j + 1] - bones[j].position;
                if (from.sqrMagnitude > 1e-12f && to.sqrMagnitude > 1e-12f)
                    bones[j].rotation = Quaternion.FromToRotation(from, to) * bones[j].rotation;
            }

            Vector3 line = Vector3.ProjectOnPlane(straightTip - goal[0], Vector3.up);
            Vector3 tail = Vector3.ProjectOnPlane(point[n] - point[0], Vector3.up);
            TipBend = line.sqrMagnitude > 1e-8f && tail.sqrMagnitude > 1e-8f ? -Vector3.SignedAngle(line, tail, Vector3.up) : 0f;
            Vector3 hipsLine = straightTip - goal[0], dorsal = bones[0].parent.TransformDirection(upLocal);
            Vector3 side = Vector3.Cross(dorsal, hipsLine);
            TipLift = side.sqrMagnitude > 1e-10f
                ? -Vector3.SignedAngle(hipsLine, Vector3.ProjectOnPlane(point[n] - point[0], side), side)
                : 0f;

            if (legs == null || !legs.Ready) return;
            float overlap = 0f;
            for (int j = 1; j <= n; j++)
                for (int k = 1; k <= BallsPerBone; k++)
                    overlap = Mathf.Max(overlap, legs.Depth(Vector3.Lerp(point[j - 1], point[j], (float)k / BallsPerBone), Ball(j)));
            LegOverlap = overlap;
        }

        // Balls along the bone from point j-1 to point j keep out of the legs: the bone swings about its root, each ball
        // moving the far end by its push. A ball nearer the root moves less, so it clears over the next steps; clearing it
        // at once would swing the bone a long way for a small push.
        private void Collide(int j, float webStep)
        {
            Vector3 root = point[j - 1];
            for (int k = 1; k <= BallsPerBone; k++)
            {
                float t = (float)k / BallsPerBone;
                Vector3 push = legs.Push(Vector3.Lerp(root, point[j], t), Ball(j), webStep);
                if (push == Vector3.zero) continue;
                Vector3 d = point[j] + push - root;
                if (d.sqrMagnitude > 1e-12f) point[j] = root + d.normalized * length[j - 1];
            }
        }

        // Point j at its bone's length from the point before, bent at most maxBend from the way along there
        private Vector3 Hang(int j, Vector3 at, Vector3 before, Vector3 along)
        {
            Vector3 d = at - before;
            if (d.sqrMagnitude < 1e-12f) d = along;
            d = Vector3.RotateTowards(along.normalized, d.normalized, maxBend * Mathf.Deg2Rad, 0f);
            return before + d * length[j - 1];
        }

        // the balls along the bone from point j-1 to point j
        private float Ball(int j) => radius[j - 1] * thickness;

        private void OnDrawGizmosSelected()
        {
            if (point == null || radius == null) return;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
            for (int j = 1; j < point.Length; j++)
            {
                Gizmos.DrawLine(point[j - 1], point[j]);
                for (int k = 1; k <= BallsPerBone; k++)
                    Gizmos.DrawWireSphere(Vector3.Lerp(point[j - 1], point[j], (float)k / BallsPerBone), Ball(j));
            }
        }

        // The bones at rest on the hips, each bent by the given angle about world up; the points they make.
        private void Shape(float bendPerJoint)
        {
            int n = bones.Count;
            for (int j = 0; j < n; j++) bones[j].localRotation = rest[j];
            straightTip = bones[n - 1].TransformPoint(tipLocal);
            if (bendPerJoint != 0f)
                for (int j = 0; j < n; j++) bones[j].rotation = Quaternion.AngleAxis(bendPerJoint, Vector3.up) * bones[j].rotation;
            for (int j = 0; j < n; j++) goal[j] = bones[j].position;
            goal[n] = bones[n - 1].TransformPoint(tipLocal);
        }

    }
}
