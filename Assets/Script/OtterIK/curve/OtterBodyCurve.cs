using UnityEngine;
using OtterIK.Neo.Experiment;

namespace OtterIK.Curve
{
    /// <summary>
    /// Drop-in replacement for ExpShadowSpineHarmonizer that keeps its inputs and tuning and only changes
    /// how the back of the body follows the chest.
    ///
    /// It evaluates the harmonizer's own provider list (Aim toward the head IK guide, FlipRoll, KickWave) and
    /// mixes / cascades them with the harmonizer's flexibility and damping values, so the chest leads, the kick
    /// wave travels and the flip rolls exactly as before. The neck is left to ExpShadowNeckController.
    /// - BackMode.LegacyCascade + segmentFollowsOwnBone off reproduces the harmonizer.
    /// - BackMode.FollowChestPath: nodes behind the chest take the heading the chest had a moment ago, so the
    ///   hips follow the chest's path instead of inheriting its yaw and swinging out of the turn.
    /// Pitch and roll come from the same cascade in both modes.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public class OtterBodyCurve : MonoBehaviour
    {
        public enum BackMode { LegacyCascade, FollowChestPath }

        [Header("References")]
        public SpineDecouplerWithShadows decoupler;
        [Tooltip("Providers and tuning are read from this component. It is disabled while this one is active.")]
        public ExpShadowSpineHarmonizer legacyHarmonizer;
        [Tooltip("Only used for speed (shorter trail when swimming).")]
        public MovementControllerRB movement;

        [Header("Back of the body")]
        public BackMode backMode = BackMode.FollowChestPath;
        [Tooltip("Delay (s) between the chest and the hip when nearly stopped (turning in place).")]
        [Min(0f)] public float trailTimeIdle = 0.35f;
        [Tooltip("Delay (s) between the chest and the hip at swim speed.")]
        [Min(0f)] public float trailTimeSwim = 0.2f;

        [Header("Segment placement")]
        [Tooltip("On: each segment points along its own bone. Off: legacy placement, where each node is offset by the rotation of the node in front of it.")]
        public bool segmentFollowsOwnBone = true;

        [Header("Debug")]
        public bool drawGizmos = true;

        /// <summary>Chest yaw relative to the body (deg), i.e. how far the front leads the rigidbody.</summary>
        public float DebugLeadDeg { get; private set; }
        public float DebugTrailTime { get; private set; }

        private struct HeadingSample { public float t; public float yaw; }

        private Transform root;
        private Transform[] shadows;
        private Vector3[] restLocalPos;
        private Vector3[] restOffset;   // root-local vector from node i+1 to node i at rest
        private float[] backDist01;     // arc distance from the chest, 0 = chest, 1 = hip

        private Quaternion[] ypTargets;
        private float[] rollTargets;
        private Quaternion[] filteredYP;
        private float[] filteredRoll;
        private Quaternion[] localRot;

        private bool built;
        private bool started;
        private bool harmonizerWasEnabled;

        private readonly HeadingSample[] history = new HeadingSample[512];
        private int historyHead = -1;
        private int historyCount;
        private float rootYaw;
        private float lastRawRootYaw;
        private bool hasRootYaw;

        private void Reset()
        {
            decoupler = GetComponent<SpineDecouplerWithShadows>();
            legacyHarmonizer = GetComponent<ExpShadowSpineHarmonizer>();
            movement = GetComponentInParent<MovementControllerRB>();
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
            if (decoupler == null || decoupler.shadowNodes == null || decoupler.shadowNodes.Count < 2 || legacyHarmonizer == null)
            {
                Debug.LogWarning("[OtterBodyCurve] Needs the decoupler shadows and the legacy harmonizer (for providers and tuning); not driving.", this);
                return;
            }
            if (movement == null) movement = GetComponentInParent<MovementControllerRB>();

            harmonizerWasEnabled = legacyHarmonizer.enabled;
            legacyHarmonizer.enabled = false;

            int n = decoupler.shadowNodes.Count;
            if (shadows == null || shadows.Length != n)
            {
                shadows = decoupler.shadowNodes.ToArray();
                root = shadows[0].parent;
                restLocalPos = new Vector3[n];
                restOffset = new Vector3[n];
                backDist01 = new float[n];
                for (int i = 0; i < n; i++) restLocalPos[i] = shadows[i].localPosition;

                float total = 0f;
                for (int i = n - 2; i >= 0; i--)
                {
                    restOffset[i] = restLocalPos[i] - restLocalPos[i + 1];
                    total += restOffset[i].magnitude;
                    backDist01[i] = total;
                }
                for (int i = 0; i < n; i++) backDist01[i] = total > 1e-6f ? backDist01[i] / total : 0f;

                ypTargets = new Quaternion[n];
                rollTargets = new float[n];
                filteredYP = new Quaternion[n];
                filteredRoll = new float[n];
                localRot = new Quaternion[n];

                if (Vector3.Dot(root.up, Vector3.up) < 0.99f)
                    Debug.LogWarning($"[OtterBodyCurve] Shadow root '{root.name}' up axis is not world up; yaw is measured about its local up.", this);
            }

            for (int i = 0; i < n; i++)
            {
                filteredYP[i] = Quaternion.identity;
                filteredRoll[i] = 0f;
            }
            historyCount = 0;
            historyHead = -1;
            hasRootYaw = false;
            built = true;
        }

        private void Teardown()
        {
            if (!built) return;
            built = false;

            for (int i = 0; i < shadows.Length; i++)
            {
                if (shadows[i] == null) continue;
                shadows[i].localRotation = Quaternion.identity;
                shadows[i].localPosition = restLocalPos[i];
            }
            if (legacyHarmonizer != null) legacyHarmonizer.enabled = harmonizerWasEnabled;
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (!built || root == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            int n = shadows.Length;

            MixProviders(n);
            Cascade(n, dt);

            float rawRootYaw = YawOf(root.forward);
            rootYaw = hasRootYaw ? rootYaw + Mathf.DeltaAngle(lastRawRootYaw, rawRootYaw) : rawRootYaw;
            lastRawRootYaw = rawRootYaw;
            hasRootYaw = true;

            float now = Time.time;
            float chestYaw = TwistYawDeg(filteredYP[n - 1]);
            DebugLeadDeg = chestYaw;
            PushHeading(now, rootYaw + chestYaw);

            float speed01 = movement != null ? Mathf.Clamp01(movement.GetSpeed() / Mathf.Max(0.01f, movement.GetSwimSpeed())) : 0f;
            float trailTime = Mathf.Lerp(trailTimeIdle, trailTimeSwim, speed01);
            DebugTrailTime = trailTime;

            for (int i = 0; i < n; i++)
            {
                Quaternion yp = filteredYP[i];
                if (backMode == BackMode.FollowChestPath && i < n - 1)
                {
                    Quaternion twist = Quaternion.AngleAxis(TwistYawDeg(yp), Vector3.up);
                    Quaternion swing = Quaternion.Inverse(twist) * yp;
                    float yaw = SampleHeading(now - trailTime * backDist01[i]) - rootYaw;
                    yp = Quaternion.AngleAxis(yaw, Vector3.up) * swing;
                }
                localRot[i] = yp * Quaternion.Euler(0f, 0f, filteredRoll[i]);
            }

            shadows[n - 1].localRotation = localRot[n - 1];
            shadows[n - 1].localPosition = restLocalPos[n - 1];
            for (int i = n - 2; i >= 0; i--)
            {
                Quaternion segRot = segmentFollowsOwnBone ? localRot[i] : localRot[i + 1];
                shadows[i].localRotation = localRot[i];
                shadows[i].localPosition = shadows[i + 1].localPosition + segRot * restOffset[i];
            }
        }

        /// <summary>Same blending as ExpShadowSpineHarmonizer stage 1.</summary>
        private void MixProviders(int n)
        {
            var providers = legacyHarmonizer.providers;
            for (int i = 0; i < n; i++)
            {
                Vector3 ypRotVecSum = Vector3.zero;
                float ypWeightSum = 0f;
                float dominantRoll = 0f;

                if (providers != null)
                {
                    foreach (var prov in providers)
                    {
                        if (prov == null || !prov.isActiveAndEnabled) continue;
                        if (!prov.Evaluate(i, null, shadows[i], Quaternion.identity, out Quaternion targetRot, out float w)) continue;

                        float finalW = Mathf.Clamp01(w * prov.globalWeight);
                        if (finalW <= 1e-5f) continue;

                        Vector3 euler = targetRot.eulerAngles;
                        float r = Normalize180(euler.z);
                        if (Mathf.Abs(r) > Mathf.Abs(dominantRoll)) dominantRoll = r;

                        Quaternion ypOnly = Quaternion.Euler(euler.x, euler.y, 0f);
                        if (Quaternion.Angle(Quaternion.identity, ypOnly) > 0.1f)
                        {
                            ypOnly.ToAngleAxis(out float angle, out Vector3 axis);
                            if (angle > 180f) angle -= 360f;
                            ypRotVecSum += axis.normalized * (angle * Mathf.Deg2Rad) * finalW;
                            ypWeightSum += finalW;
                        }
                    }
                }

                rollTargets[i] = dominantRoll;
                if (ypWeightSum > 1e-6f)
                {
                    Vector3 meanVec = (ypRotVecSum / ypWeightSum) * Mathf.Clamp01(ypWeightSum);
                    ypTargets[i] = Quaternion.AngleAxis(meanVec.magnitude * Mathf.Rad2Deg, meanVec.normalized);
                }
                else
                {
                    ypTargets[i] = Quaternion.identity;
                }
            }
        }

        /// <summary>Same chest-to-hip filtering as ExpShadowSpineHarmonizer stage 2.</summary>
        private void Cascade(int n, float dt)
        {
            var h = legacyHarmonizer;
            float ypAlpha = 1f - Mathf.Exp(-0.693f * dt / h.dampingHalfLife);
            float rAlpha = 1f - Mathf.Exp(-0.693f * dt / h.rollDampingHalfLife);
            for (int i = n - 1; i >= 0; i--)
            {
                Quaternion ypTarget = ypTargets[i];
                if (i < n - 1) ypTarget = Quaternion.Slerp(ypTarget, filteredYP[i + 1], h.flexibility);
                filteredYP[i] = Quaternion.Slerp(filteredYP[i], ypTarget, ypAlpha);

                float rTarget = rollTargets[i];
                if (i < n - 1) rTarget = Mathf.LerpAngle(rTarget, filteredRoll[i + 1], h.rollFlexibility);
                filteredRoll[i] = Mathf.LerpAngle(filteredRoll[i], rTarget, rAlpha);
            }
        }

        // ------------------------------------------------------------------ chest heading history

        private void PushHeading(float t, float yaw)
        {
            historyHead = (historyHead + 1) % history.Length;
            history[historyHead] = new HeadingSample { t = t, yaw = yaw };
            historyCount = Mathf.Min(historyCount + 1, history.Length);
        }

        private float SampleHeading(float t)
        {
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

        /// <summary>Angle of the rotation about +Y contained in q (q = twist * swing).</summary>
        private static float TwistYawDeg(Quaternion q) => Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(q.y, q.w) * Mathf.Rad2Deg);

        private static float Normalize180(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }

        private void OnDrawGizmos()
        {
            if (!drawGizmos || !built || shadows == null) return;
            for (int i = 0; i < shadows.Length - 1; i++)
            {
                Gizmos.color = Color.Lerp(new Color(1f, 0.5f, 0.1f), new Color(0.2f, 0.6f, 1f), backDist01[i]);
                Gizmos.DrawLine(shadows[i].position, shadows[i + 1].position);
            }
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(shadows[shadows.Length - 1].position, 0.03f);
        }
    }
}
