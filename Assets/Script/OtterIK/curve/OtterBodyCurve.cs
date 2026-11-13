using System.Collections.Generic;
using UnityEngine;
using OtterIK.Neo.Experiment;

namespace OtterIK.Curve
{
    /// <summary>
    /// Drives the spine + neck as one continuous curve, writing the decoupled shadow nodes directly.
    ///
    /// s runs along the body: 0 = head tip, 1 = hip. One node (the pivot) stays fixed in the body frame.
    /// - In front of the pivot the body shows the near future: where intent is about to turn it.
    ///   This is what makes the head and chest move before the rigidbody does.
    /// - Behind the pivot the body shows the recent past: each node takes the heading the body had
    ///   (s - s_pivot) * trailTime seconds ago, so the hips follow the path the chest took.
    ///
    /// A/B against the legacy Harmonizer by toggling this component before entering Play mode.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public class OtterBodyCurve : MonoBehaviour
    {
        [Header("References")]
        public SpineDecouplerWithShadows decoupler;
        public MovementControllerRB movement;
        [Tooltip("Body frame (yaw-only rigidbody root). Defaults to the MovementControllerRB transform.")]
        public Transform body;
        [Tooltip("Neck / head joints from the chest outward. Auto-filled from ExpShadowNeckController on Reset.")]
        public Transform[] neckJoints;

        [Header("Legacy drivers (disabled while this component is active)")]
        public ExpShadowSpineHarmonizer legacyHarmonizer;
        public ExpShadowNeckController legacyNeck;

        [Header("Pivot")]
        [Tooltip("Spine joint index (0 = hip) that stays fixed in the body frame. -1 = middle of the spine.")]
        public int pivotSpineIndex = -1;

        [Header("Front: lead toward intent (future)")]
        [Tooltip("How far into the future the head tip shows (seconds).")]
        [Min(0f)] public float leadTime = 0.25f;
        [Tooltip("Turn rate used to extrapolate where the body will be heading (deg/s).")]
        [Min(0f)] public float leadTurnRate = 220f;
        [Tooltip("Half-life (s) of the intent signal smoothing.")]
        [Min(0.001f)] public float intentHalfLife = 0.06f;

        [Header("Back: follow heading history (past)")]
        [Tooltip("Delay (s) between the pivot and the hip when nearly stopped (turning in place).")]
        [Min(0f)] public float trailTimeIdle = 0.35f;
        [Tooltip("Delay (s) between the pivot and the hip at swim speed. Faster swimming = tighter follow.")]
        [Min(0f)] public float trailTimeSwim = 0.2f;

        [Header("Neck look")]
        [Tooltip("Extra pull of the neck toward the intent direction, on top of the curve (0 = curve only).")]
        [Range(0f, 1f)] public float neckLook = 0.6f;
        [Min(0f)] public float maxHeadLookDeg = 80f;

        [Header("Limits")]
        [Tooltip("Max yaw difference between neighbouring nodes (deg).")]
        [Min(0f)] public float maxBendPerJoint = 30f;

        [Header("Debug")]
        public bool drawGizmos = true;

        public float DebugLeadDeg => lead;
        public float DebugTrailTime { get; private set; }

        private class Node
        {
            public Transform shadow;
            public Transform joint;
            public bool ownsShadow;
            public Transform originalParent;
            public Quaternion restRel;  // shadow rotation in body space at rest
            public Vector3 restPos;     // shadow position in body space at rest
            public Vector3 seg;         // body-space vector to the next node (toward the head) at rest
            public float s;             // 0 = head tip, 1 = hip
            public float sMid;          // s at the middle of this node's segment (what its rotation represents)
            public float yaw;           // body-space yaw this frame (deg)
        }

        private struct HeadingSample { public float t; public float yaw; }

        private readonly List<Node> nodes = new List<Node>();
        private Quaternion[] rot;
        private Vector3[] pos;
        private int pivot;
        private int neckStart;
        private bool built;
        private bool started;
        private bool restCached;
        private bool legacyHarmonizerWasEnabled;
        private bool legacyNeckWasEnabled;

        private readonly HeadingSample[] history = new HeadingSample[512];
        private int historyHead = -1;
        private int historyCount;
        private float unwrappedYaw;
        private float lastRawYaw;
        private bool hasYaw;
        private float lead;

        private void Reset()
        {
            decoupler = GetComponent<SpineDecouplerWithShadows>();
            movement = GetComponentInParent<MovementControllerRB>();
            if (movement != null) body = movement.transform;
            legacyHarmonizer = GetComponent<ExpShadowSpineHarmonizer>();
            legacyNeck = GetComponent<ExpShadowNeckController>();
            if (legacyNeck != null && legacyNeck.neckJoints != null)
                neckJoints = (Transform[])legacyNeck.neckJoints.Clone();
        }

        private void Start()
        {
            started = true;
            Build();
        }

        private void OnEnable()
        {
            if (started) Build();
        }

        private void OnDisable()
        {
            Teardown();
        }

        // ------------------------------------------------------------------ build / teardown

        private void Build()
        {
            if (built) return;
            if (movement == null) movement = GetComponentInParent<MovementControllerRB>();
            if (body == null && movement != null) body = movement.transform;
            if (decoupler == null || body == null || decoupler.shadowNodes == null || decoupler.shadowNodes.Count < 2)
            {
                Debug.LogWarning("[OtterBodyCurve] Missing decoupler shadows or body; not driving.", this);
                return;
            }

            if (legacyHarmonizer != null)
            {
                legacyHarmonizerWasEnabled = legacyHarmonizer.enabled;
                legacyHarmonizer.enabled = false;
            }
            if (legacyNeck != null)
            {
                legacyNeckWasEnabled = legacyNeck.enabled;
                // Its OnDisable hands the neck joints back to their original parent (the chest bone).
                legacyNeck.enabled = false;
            }

            var spineShadows = decoupler.shadowNodes;
            if (restCached) PoseSpineAtRest();
            else nodes.Clear();

            if (!restCached)
            {
                for (int i = 0; i < spineShadows.Count; i++)
                {
                    nodes.Add(new Node
                    {
                        shadow = spineShadows[i],
                        joint = decoupler.spineJoints != null && i < decoupler.spineJoints.Length ? decoupler.spineJoints[i] : null,
                    });
                }
            }
            else
            {
                nodes.RemoveRange(spineShadows.Count, nodes.Count - spineShadows.Count);
            }
            neckStart = spineShadows.Count;

            Transform runtimeRoot = spineShadows[0].parent;
            if (neckJoints != null)
            {
                foreach (var joint in neckJoints)
                {
                    if (joint == null) continue;
                    var shadow = new GameObject(joint.name + "_CurveShadow").transform;
                    shadow.SetParent(runtimeRoot, false);
                    shadow.position = joint.position;
                    shadow.localRotation = Quaternion.identity;
                    shadow.localScale = Vector3.one;

                    var node = new Node { shadow = shadow, joint = joint, ownsShadow = true, originalParent = joint.parent };
                    joint.SetParent(shadow, true);
                    joint.localPosition = Vector3.zero;
                    nodes.Add(node);
                }
            }

            CacheRest();
            pivot = Mathf.Clamp(pivotSpineIndex >= 0 ? pivotSpineIndex : spineShadows.Count / 2, 0, spineShadows.Count - 1);
            built = true;
        }

        private void CacheRest()
        {
            Quaternion bodyInv = Quaternion.Inverse(body.rotation);
            int start = restCached ? neckStart : 0;
            for (int i = start; i < nodes.Count; i++)
            {
                nodes[i].restRel = bodyInv * nodes[i].shadow.rotation;
                nodes[i].restPos = body.InverseTransformPoint(nodes[i].shadow.position);
            }

            float total = 0f;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 next = i + 1 < nodes.Count ? body.TransformPoint(nodes[i + 1].restPos) : body.TransformPoint(nodes[i].restPos);
                nodes[i].seg = bodyInv * (next - body.TransformPoint(nodes[i].restPos));
                total += nodes[i].seg.magnitude;
            }

            // Arc length from the hip, then flip so the head tip is s = 0.
            float acc = 0f;
            for (int i = 0; i < nodes.Count; i++)
            {
                float len = nodes[i].seg.magnitude;
                nodes[i].s = total > 1e-6f ? 1f - acc / total : 0f;
                nodes[i].sMid = total > 1e-6f ? 1f - (acc + 0.5f * len) / total : 0f;
                acc += len;
            }
            restCached = true;
        }

        private void PoseSpineAtRest()
        {
            for (int i = 0; i < neckStart && i < nodes.Count; i++)
            {
                nodes[i].shadow.SetPositionAndRotation(body.TransformPoint(nodes[i].restPos), body.rotation * nodes[i].restRel);
            }
        }

        private void Teardown()
        {
            if (!built) return;
            built = false;

            if (gameObject.activeInHierarchy && body != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                    nodes[i].shadow.SetPositionAndRotation(body.TransformPoint(nodes[i].restPos), body.rotation * nodes[i].restRel);

                for (int i = neckStart; i < nodes.Count; i++)
                {
                    var n = nodes[i];
                    if (n.joint != null && n.originalParent != null) n.joint.SetParent(n.originalParent, true);
                    if (n.ownsShadow && n.shadow != null) Destroy(n.shadow.gameObject);
                }
            }

            if (legacyHarmonizer != null) legacyHarmonizer.enabled = legacyHarmonizerWasEnabled;
            if (legacyNeck != null) legacyNeck.enabled = legacyNeckWasEnabled;
            historyCount = 0;
            historyHead = -1;
            hasYaw = false;
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (!built) return;
            float dt = Time.deltaTime;
            if (dt <= 1e-6f) return;
            float now = Time.time;

            float rawYaw = YawOf(body.forward);
            unwrappedYaw = hasYaw ? unwrappedYaw + Mathf.DeltaAngle(lastRawYaw, rawYaw) : rawYaw;
            lastRawYaw = rawYaw;
            hasYaw = true;
            PushHeading(now, unwrappedYaw);

            UpdateLead(rawYaw, dt);

            float speed01 = movement != null ? Mathf.Clamp01(movement.GetSpeed() / Mathf.Max(0.01f, movement.GetSwimSpeed())) : 0f;
            float trailTime = Mathf.Lerp(trailTimeIdle, trailTimeSwim, speed01);
            DebugTrailTime = trailTime;

            float sPivot = nodes[pivot].s;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                float s = i == nodes.Count - 1 ? n.s : n.sMid;
                if (s < sPivot)
                {
                    float ahead = sPivot > 1e-4f ? leadTime * (sPivot - s) / sPivot : 0f;
                    float reach = leadTurnRate * ahead;
                    n.yaw = Mathf.Clamp(lead, -reach, reach);
                }
                else
                {
                    float behind = sPivot < 1f - 1e-4f ? trailTime * (s - sPivot) / (1f - sPivot) : 0f;
                    n.yaw = SampleHeading(now - behind) - unwrappedYaw;
                }
            }

            ApplyNeckLook();
            ApplyBendLimits();
            WritePose();
        }

        private void UpdateLead(float rawYaw, float dt)
        {
            float target = 0f;
            if (movement != null)
            {
                Vector3 intent = movement.GetWorldDir();
                intent.y = 0f;
                if (intent.sqrMagnitude > 1e-6f)
                {
                    target = Mathf.DeltaAngle(rawYaw, YawOf(intent));

                    // Near a full reversal, keep the head on the side the body is actually turning toward
                    // (or the side it is already on), instead of snapping across at +-180.
                    float steer = Mathf.DeltaAngle(rawYaw, YawOf(movement.GetSteerDir()));
                    float side = Mathf.Abs(steer) > 1f ? Mathf.Sign(steer) : (Mathf.Abs(lead) > 1f ? Mathf.Sign(lead) : 0f);
                    if (Mathf.Abs(target) > 120f && side != 0f && Mathf.Sign(target) != side)
                        target += 360f * side;

                    target *= movement.GetAimBlend01();
                }
            }
            lead = Mathf.Lerp(lead, target, HalfLifeAlpha(intentHalfLife, dt));
        }

        private void ApplyNeckLook()
        {
            int count = nodes.Count - neckStart;
            if (count <= 0 || neckLook <= 0f) return;
            float look = Mathf.Clamp(lead, -maxHeadLookDeg, maxHeadLookDeg);
            for (int j = 0; j < count; j++)
            {
                var n = nodes[neckStart + j];
                float w = (j + 1f) / count;
                n.yaw = Mathf.Lerp(n.yaw, look, neckLook * w);
            }
        }

        private void ApplyBendLimits()
        {
            for (int i = pivot + 1; i < nodes.Count; i++)
                nodes[i].yaw = Mathf.Clamp(nodes[i].yaw, nodes[i - 1].yaw - maxBendPerJoint, nodes[i - 1].yaw + maxBendPerJoint);
            for (int i = pivot - 1; i >= 0; i--)
                nodes[i].yaw = Mathf.Clamp(nodes[i].yaw, nodes[i + 1].yaw - maxBendPerJoint, nodes[i + 1].yaw + maxBendPerJoint);
        }

        private void WritePose()
        {
            Quaternion bodyRot = body.rotation;
            int n = nodes.Count;
            if (rot == null || rot.Length != n)
            {
                rot = new Quaternion[n];
                pos = new Vector3[n];
            }
            for (int i = 0; i < n; i++)
                rot[i] = Quaternion.AngleAxis(nodes[i].yaw, Vector3.up);

            pos[pivot] = body.TransformPoint(nodes[pivot].restPos);
            for (int i = pivot; i < n - 1; i++)
                pos[i + 1] = pos[i] + bodyRot * (rot[i] * nodes[i].seg);
            for (int i = pivot - 1; i >= 0; i--)
                pos[i] = pos[i + 1] - bodyRot * (rot[i] * nodes[i].seg);

            for (int i = 0; i < n; i++)
                nodes[i].shadow.SetPositionAndRotation(pos[i], bodyRot * rot[i] * nodes[i].restRel);
        }

        // ------------------------------------------------------------------ heading history

        private void PushHeading(float t, float yaw)
        {
            historyHead = (historyHead + 1) % history.Length;
            history[historyHead] = new HeadingSample { t = t, yaw = yaw };
            historyCount = Mathf.Min(historyCount + 1, history.Length);
        }

        private float SampleHeading(float t)
        {
            if (historyCount == 0) return unwrappedYaw;
            HeadingSample newer = history[historyHead];
            if (t >= newer.t) return newer.yaw;
            for (int k = 1; k < historyCount; k++)
            {
                HeadingSample older = history[(historyHead - k + history.Length) % history.Length];
                if (older.t <= t)
                {
                    float span = newer.t - older.t;
                    float a = span > 1e-6f ? (t - older.t) / span : 0f;
                    return Mathf.Lerp(older.yaw, newer.yaw, a);
                }
                newer = older;
            }
            return newer.yaw;
        }

        // ------------------------------------------------------------------ helpers

        private static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        private static float HalfLifeAlpha(float halfLife, float dt) => 1f - Mathf.Exp(-0.693147f * dt / Mathf.Max(1e-4f, halfLife));

        private void OnDrawGizmos()
        {
            if (!drawGizmos || !built || nodes.Count < 2) return;
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                Gizmos.color = Color.Lerp(new Color(1f, 0.5f, 0.1f), new Color(0.2f, 0.6f, 1f), nodes[i].s);
                Gizmos.DrawLine(nodes[i].shadow.position, nodes[i + 1].shadow.position);
            }
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(nodes[pivot].shadow.position, 0.03f);
        }
    }
}
