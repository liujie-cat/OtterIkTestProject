using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Drives the tail bones (OtterRig.TailRoot and the tail bones below it). Each joint is a point dragged through still
    /// water behind the hips and held to the tail's shape by a spring that softens toward the tip, so the tail trails
    /// the path, lags the hips' swings and whips back after them, and the spine's fluke wave runs on down it. As a
    /// rudder it bends toward the side the otter turns to, following the brain's turn rate so it leads the body, and it
    /// sways toward each hind foot as that foot pushes.
    /// </summary>
    [DefaultExecutionOrder(320)]
    [DisallowMultipleComponent]
    public class TailRudder : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
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
        [Tooltip("Sway toward a hind foot through its push, over the whole tail (deg).")]
        public float kickSway = 22f;

        private readonly List<Transform> bones = new List<Transform>();
        private Quaternion[] rest;
        private Vector3 tipLocal, straightTip;
        // points 0..n-1 at the bones, n past the last bone's end; point 0 rides on the hips
        private Vector3[] point, velocity, goal, lastGoal;
        private float[] length;
        private float turn;
        private bool settled;

        /// <summary>The tail's bend from the hips' line at the tip (deg, + = to the otter's right in the world, seen from above).</summary>
        public float TipBend { get; private set; }
        /// <summary>The rudder's share of the bend asked for (-1..1 of a full turn, + = right).</summary>
        public float Rudder => turn;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
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
            Transform last = bones[n - 1];
            tipLocal = last.InverseTransformPoint(last.position + 0.8f * (last.position - bones[n - 2].position));
            point = new Vector3[n + 1];
            velocity = new Vector3[n + 1];
            goal = new Vector3[n + 1];
            lastGoal = new Vector3[n + 1];
            length = new float[n];
            Shape(0f);
            for (int j = 0; j < n; j++) length[j] = Vector3.Distance(goal[j], goal[j + 1]);
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
            Shape(-(rudder * turn + kickSway * HindSway()) / n);

            if (!settled || (goal[n] - point[n]).sqrMagnitude > 1f)
            {
                for (int j = 0; j <= n; j++)
                {
                    point[j] = lastGoal[j] = goal[j];
                    velocity[j] = Vector3.zero;
                }
                settled = true;
            }

            int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 16);
            float h = dt / steps;
            for (int s = 1; s <= steps; s++)
            {
                float a = (float)s / steps;
                point[0] = Vector3.Lerp(lastGoal[0], goal[0], a);
                for (int j = 1; j <= n; j++)
                {
                    // a spring to the shape, damped against the still water; the tip's lags most
                    float tau = lag * j / n;
                    float w = 2f * damping / tau;
                    Vector3 target = Vector3.Lerp(lastGoal[j], goal[j], a);
                    Vector3 before = point[j];
                    velocity[j] += (w * w * (target - point[j]) - 2f * damping * w * velocity[j]) * h;
                    point[j] += velocity[j] * h;

                    Vector3 along = j == 1
                        ? Vector3.Lerp(lastGoal[1], goal[1], a) - point[0]
                        : point[j - 1] - point[j - 2];
                    Vector3 d = point[j] - point[j - 1];
                    if (d.sqrMagnitude < 1e-12f) d = along;
                    d = Vector3.RotateTowards(along.normalized, d.normalized, maxBend * Mathf.Deg2Rad, 0f);
                    point[j] = point[j - 1] + d * length[j - 1];
                    velocity[j] = (point[j] - before) / h;
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

        // -1..1: the right hind foot's push less the left's, each a bump over its power stroke
        private float HindSway()
        {
            if (brain == null || !brain.HasLimbGeometry) return 0f;
            var rhythm = brain.Rhythm.limbs;
            float ahead = Time.time - Time.fixedTime;
            return Push(rhythm[(int)LimbId.HR], ahead) - Push(rhythm[(int)LimbId.HL], ahead);
        }

        private static float Push(in LimbStroke st, float ahead)
        {
            if (!st.active || st.parking || st.drive <= 0f) return 0f;
            float phase = st.phase + st.rate * ahead;
            if (phase >= st.powerFraction) return 0f;
            return Mathf.Sin(Mathf.PI * phase / Mathf.Max(1e-3f, st.powerFraction)) * Mathf.Lerp(LightSway, 1f, st.drive);
        }

        // a light cruising kick still sways the tail this much of a full one's
        private const float LightSway = 0.6f;
    }
}
