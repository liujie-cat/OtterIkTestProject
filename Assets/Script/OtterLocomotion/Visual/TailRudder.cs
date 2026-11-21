using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Drives the tail bones (OtterRig.TailRoot and the tail bones below it). A sea otter's tail is short, thick and
    /// stiff: it bends at a few joints only and the bones between them hold their shape. Each joint's end is a point
    /// dragged through still water behind the hips and held to the tail's shape by a spring, so the tail trails the
    /// path and follows the hips' swings a little late, without whipping. As a rudder it bends toward the side the
    /// otter turns to, following the brain's turn rate so it leads the body, and it swings a little away from each hind
    /// foot as that foot pushes, with the curl of the body (HindBend). It is posed after the legs and keeps out of them
    /// (HindLegColliders): a leg pressed on it turns it aside about its joints, the turn easing in and back out, so a
    /// leg sweeping past nudges it rather than knocking it about. The springs hang the tail as if the legs were not
    /// there; the turn aside is laid over them. In the dolphin wave the joints lift it onto the track the body swims
    /// along (DolphinTrack), so it follows the hips through the water as the rest of the body does.
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
        [Tooltip("How quickly the tail gives way to a leg pressed on it (s), and settles back once the leg has gone (s): slower than the kicks come, so kicking it rides on them rather than bobbing with each.")]
        public float giveTime = 0.04f;
        public float settleTime = 0.6f;
        [Tooltip("Most a leg turns a piece of the tail aside (deg): the tail is heavy in the water and only yields a little; a leg pressing further sinks into it rather than levering it up.")]
        public float maxAside = 15f;
        [Tooltip("Joints the tail bends at, spread down it from the root; the bones between them hold their shape.")]
        [Range(1, 6)] public int joints = 2;
        [Tooltip("How long the tip takes to follow the hips (s); joints nearer the root follow sooner.")]
        [Range(0.005f, 0.3f)] public float lag = 0.05f;
        [Tooltip("Damping of each joint's spring (1 = no overshoot; lower = a livelier whip after the hips stop).")]
        [Range(0.2f, 1.5f)] public float damping = 1f;
        [Tooltip("Most a joint may bend from the one before it (deg).")]
        public float maxBend = 20f;

        [Header("Rudder")]
        [Tooltip("Bend toward the side turned to at a full turn, over the whole tail (deg; negative bends it away).")]
        public float rudder = 25f;
        [Tooltip("Turn rate that counts as a full turn (deg/s).")]
        public float fullTurnRate = 180f;
        [Tooltip("How quickly the rudder follows the turn (s).")]
        public float rudderTime = 0.12f;
        [Tooltip("Sway with the hind pushes, over the whole tail (deg): each push curls the body away from the pushing foot (HindBend), and the tail tip goes with the curl.")]
        public float kickSway = 6f;
        [Tooltip("Yaw moment of a push (N·m) that sways the tail most of the way.")]
        public float fullMoment = 0.05f;

        [Header("Dolphin wave")]
        [Tooltip("Found on this object when empty; without it the tail only trails the wave.")]
        public DolphinTrack track;

        private List<Transform> bones = new List<Transform>();
        // the body's dorsal side in the space of the bone the tail hangs from
        private Vector3 upLocal;
        private Quaternion[] rest;
        private Vector3 tipLocal, straightTip;
        // the bones the joints are at, root first
        private int[] knot;
        // points 0..k-1 at the joints, k past the last bone's end; point 0 rides on the hips. shown: the same turned
        // aside out of the legs, as the bones are posed
        private Vector3[] point, velocity, goal, lastGoal, shown;
        // per stiff piece, the legs' turn of it about its joint, as an angle times its axis (deg) in the hips' space
        private Vector3[] aside;
        // per stiff piece, from its joint to the next point: its length; the balls along it, as thick as it is high,
        // in a row down its middle and, as it is flat, a row out at either side
        private float[] length, radius, spread;
        private int[] balls;
        private Vector3 dorsal;
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
        /// <summary>Each piece's width x height measured from the mesh (cm, root to tip), for the logs.</summary>
        public string Describe() => radius == null ? "-" : string.Join("/", radius.Select((r, i) =>
            $"{2f * (r + spread[i]) * thickness * 100f:F1}x{2f * r * thickness * 100f:F1}"));

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (legs == null) legs = GetComponent<HindLegColliders>();
            if (track == null) track = GetComponent<DolphinTrack>();
        }

        // after SpineDecouplerWithShadows has rebuilt the spine in Awake
        private void Start()
        {
            bones = OtterRig.TailBones(transform);
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
            int k = Mathf.Clamp(joints, 1, n);
            knot = Enumerable.Range(0, k).Select(i => i * n / k).ToArray();
            point = new Vector3[k + 1];
            velocity = new Vector3[k + 1];
            goal = new Vector3[k + 1];
            lastGoal = new Vector3[k + 1];
            shown = new Vector3[k + 1];
            aside = new Vector3[k + 1];
            length = new float[k];
            radius = new float[k];
            spread = new float[k];
            balls = new int[k];
            Shape(0f);
            // each bone's width and height along its own stretch; the root bone's lies in the rump, so it is left out
            Vector3 up = bones[0].parent.TransformDirection(upLocal);
            var halfWidth = new float[n];
            var halfHeight = new float[n];
            for (int j = 1; j < n; j++)
            {
                Vector3 end = j + 1 < n ? bones[j + 1].position : bones[n - 1].TransformPoint(tipLocal);
                OtterRig.TryHalfExtents(bones[j], bones[j].position, end, up, out halfWidth[j], out halfHeight[j]);
            }
            for (int i = 0; i < k; i++)
            {
                length[i] = Vector3.Distance(goal[i], goal[i + 1]);
                int from = Mathf.Max(1, knot[i]), to = Mathf.Max(from, (i + 1 < k ? knot[i + 1] : n) - 1);
                float w = 0f, hh = 0f;
                for (int j = from; j <= to && j < n; j++)
                {
                    w = Mathf.Max(w, halfWidth[j]);
                    hh = Mathf.Max(hh, halfHeight[j]);
                }
                radius[i] = hh > 0f ? hh : i > 0 ? radius[i - 1] : 0.01f;
                spread[i] = Mathf.Max(0f, w - radius[i]);
                balls[i] = BallsPerBone * ((i + 1 < k ? knot[i + 1] : n) - knot[i]);
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
            int n = knot.Length;

            float turnGoal = brain != null ? Mathf.Clamp(brain.Demand.yawRateTarget / Mathf.Max(1f, fullTurnRate), -1f, 1f) : 0f;
            turn = Mathf.Lerp(turn, turnGoal, 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, rudderTime)));
            // turning about world up by a negative angle swings what trails behind to the right
            Shape(-(rudder * turn + kickSway * HindBend.Now(brain, fullMoment)) / n, track != null && track.isActiveAndEnabled && track.Weight > 1e-3f);

            if (!settled || (goal[n] - point[n]).sqrMagnitude > 1f)
            {
                for (int j = 0; j <= n; j++)
                {
                    point[j] = lastGoal[j] = goal[j];
                    velocity[j] = Vector3.zero;
                    aside[j] = Vector3.zero;
                }
                settled = true;
            }

            dorsal = bones[0].parent.TransformDirection(upLocal);
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 16);
            float h = dt / steps;
            for (int s = 1; s <= steps; s++)
            {
                float a = (float)s / steps;
                point[0] = Vector3.Lerp(lastGoal[0], goal[0], a);
                Vector3 rootAlong = Vector3.Lerp(lastGoal[1], goal[1], a) - point[0];
                for (int j = 1; j <= n; j++)
                {
                    // a spring to the shape, damped against the still water; the tip's lags most
                    float tau = lag * j / n;
                    float w = 2f * damping / tau;
                    Vector3 target = Vector3.Lerp(lastGoal[j], goal[j], a);
                    Vector3 before = point[j];
                    velocity[j] += (w * w * (target - point[j]) - 2f * damping * w * velocity[j]) * h;
                    point[j] = Hang(j, point[j] + velocity[j] * h, point[j - 1], j == 1 ? rootAlong : point[j - 1] - point[j - 2]);
                    velocity[j] = (point[j] - before) / h;
                }
            }
            for (int j = 0; j <= n; j++) lastGoal[j] = goal[j];
            GiveWay(collide && legs != null && legs.Ready, dt);

            for (int j = 0; j < n; j++)
            {
                Transform bone = bones[knot[j]];
                Vector3 end = j + 1 < n ? bones[knot[j + 1]].position : bones[bones.Count - 1].TransformPoint(tipLocal);
                Vector3 from = end - bone.position, to = shown[j + 1] - bone.position;
                if (from.sqrMagnitude > 1e-12f && to.sqrMagnitude > 1e-12f)
                    bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
            }

            Vector3 line = Vector3.ProjectOnPlane(straightTip - goal[0], Vector3.up);
            Vector3 tail = Vector3.ProjectOnPlane(shown[n] - shown[0], Vector3.up);
            TipBend = line.sqrMagnitude > 1e-8f && tail.sqrMagnitude > 1e-8f ? -Vector3.SignedAngle(line, tail, Vector3.up) : 0f;
            Vector3 hipsLine = straightTip - goal[0];
            Vector3 side = Vector3.Cross(dorsal, hipsLine);
            TipLift = side.sqrMagnitude > 1e-10f
                ? -Vector3.SignedAngle(hipsLine, Vector3.ProjectOnPlane(shown[n] - shown[0], side), side)
                : 0f;

            if (legs == null || !legs.Ready) return;
            float overlap = 0f;
            for (int j = 1; j <= n; j++)
                for (int k = 1; k <= balls[j - 1]; k++)
                    for (int lane = 0; lane < Lanes(j); lane++)
                        overlap = Mathf.Max(overlap, legs.Depth(Ball(j, k, lane, shown[j - 1], shown[j]), Ball(j)));
            LegOverlap = overlap;
        }

        // The legs turn each piece aside about its joint, the pieces below turning with it: the turn eases in while a
        // leg presses on the piece and back out once the piece would be clear without it.
        private void GiveWay(bool colliding, float dt)
        {
            Transform hips = bones[0].parent;
            float give = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, giveTime));
            float settle = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, settleTime));
            float webStep = Mathf.Max(0f, webPushSpeed) * dt;
            Quaternion carried = Quaternion.identity;
            shown[0] = point[0];
            for (int j = 1; j < point.Length; j++)
            {
                Vector3 hanging = carried * (point[j] - point[j - 1]);
                Vector3 now = Turned(hips, aside[j]) * hanging;
                Vector3 clear = colliding ? Clear(j, shown[j - 1], now, webStep) : now;
                if (Vector3.Angle(now, clear) > 1e-3f)
                {
                    Quaternion.FromToRotation(now, clear).ToAngleAxis(out float angle, out Vector3 axis);
                    if (angle > 180f) angle -= 360f;
                    aside[j] += hips.InverseTransformDirection(axis) * (angle * give);
                }
                else
                {
                    Vector3 eased = aside[j] * (1f - settle);
                    Vector3 back = Turned(hips, eased) * hanging;
                    if (!colliding || Vector3.Angle(back, Clear(j, shown[j - 1], back, webStep)) <= 1e-3f) aside[j] = eased;
                }
                aside[j] = Vector3.ClampMagnitude(aside[j], maxAside);
                Quaternion turned = Turned(hips, aside[j]);
                shown[j] = shown[j - 1] + turned * hanging;
                carried = turned * carried;
            }
        }

        private static Quaternion Turned(Transform space, Vector3 aside)
        {
            float angle = aside.magnitude;
            return angle > 1e-4f ? Quaternion.AngleAxis(angle, space.TransformDirection(aside / angle)) : Quaternion.identity;
        }

        // The piece j from root along piece, swung about its joint out of the legs, each ball moving the far end by its
        // push. A ball nearer the joint moves less, so it clears over the next frames; clearing it at once would swing
        // the piece a long way for a small push.
        private Vector3 Clear(int j, Vector3 root, Vector3 piece, float webStep)
        {
            Vector3 end = root + piece;
            for (int k = 1; k <= balls[j - 1]; k++)
                for (int lane = 0; lane < Lanes(j); lane++)
                {
                    Vector3 push = legs.Push(Ball(j, k, lane, root, end), Ball(j), webStep);
                    if (push == Vector3.zero) continue;
                    Vector3 d = end + push * ((float)k / balls[j - 1]) - root;
                    if (d.sqrMagnitude > 1e-12f) end = root + d.normalized * length[j - 1];
                }
            return end - root;
        }

        // Point j at its piece's length from the point before, bent at most maxBend from the way along there
        private Vector3 Hang(int j, Vector3 at, Vector3 before, Vector3 along)
        {
            Vector3 d = at - before;
            if (d.sqrMagnitude < 1e-12f) d = along;
            d = Vector3.RotateTowards(along.normalized, d.normalized, maxBend * Mathf.Deg2Rad, 0f);
            return before + d * length[j - 1];
        }

        // the balls along the piece from point j-1 to point j: how big, how many rows, and where ball k of a row sits
        private float Ball(int j) => radius[j - 1] * thickness;
        private int Lanes(int j) => spread[j - 1] > 1e-4f ? 3 : 1;
        private Vector3 Ball(int j, int k, int lane, Vector3 from, Vector3 to)
        {
            Vector3 at = Vector3.Lerp(from, to, (float)k / balls[j - 1]);
            Vector3 side = Vector3.Cross(dorsal, to - from);
            if (lane == 0 || side.sqrMagnitude < 1e-12f) return at;
            return at + side.normalized * ((lane == 1 ? 1f : -1f) * spread[j - 1] * thickness);
        }

        private void OnDrawGizmosSelected()
        {
            if (shown == null || radius == null) return;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
            for (int j = 1; j < shown.Length; j++)
            {
                Gizmos.DrawLine(shown[j - 1], shown[j]);
                for (int k = 1; k <= balls[j - 1]; k++)
                    for (int lane = 0; lane < Lanes(j); lane++)
                        Gizmos.DrawWireSphere(Ball(j, k, lane, shown[j - 1], shown[j]), Ball(j));
            }
        }

        // The bones at rest on the hips, each joint bent by the given angle about world up and, riding the dolphin
        // wave's track, lifted onto it; the points they make.
        private void Shape(float bendPerJoint, bool riding = false)
        {
            int n = bones.Count, k = knot.Length;
            for (int j = 0; j < n; j++) bones[j].localRotation = rest[j];
            straightTip = bones[n - 1].TransformPoint(tipLocal);
            // turning about this by a positive angle lifts the tip
            Vector3 side = Vector3.Cross(straightTip - bones[0].position, bones[0].parent.TransformDirection(upLocal));
            if (bendPerJoint != 0f)
                for (int i = 0; i < k; i++)
                {
                    Transform bone = bones[knot[i]];
                    bone.rotation = Quaternion.AngleAxis(bendPerJoint, Vector3.up) * bone.rotation;
                }
            if (riding)
            {
                // where each piece's end goes on the track, from where it trails straight back from the hips; then
                // each piece, root first, lifted about its joint to put its end there
                for (int i = 1; i <= k; i++)
                {
                    Vector3 end = End(i);
                    goal[i] = end + Vector3.up * track.AboveHipsLine(end);
                }
                for (int i = 0; i < k; i++)
                {
                    Transform bone = bones[knot[i]];
                    Vector3 piece = End(i + 1) - bone.position;
                    float length = piece.magnitude;
                    if (length < 1e-5f) continue;
                    float lift = (Mathf.Asin(Mathf.Clamp((goal[i + 1].y - bone.position.y) / length, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(piece.y / length, -1f, 1f))) * Mathf.Rad2Deg;
                    bone.rotation = Quaternion.AngleAxis(lift, side) * bone.rotation;
                }
            }
            for (int j = 0; j <= k; j++) goal[j] = End(j);
        }

        // point j's place on the bones as they are posed: a joint, or past the last bone's end
        private Vector3 End(int j) => j < knot.Length ? bones[knot[j]].position : bones[bones.Count - 1].TransformPoint(tipLocal);

    }
}
