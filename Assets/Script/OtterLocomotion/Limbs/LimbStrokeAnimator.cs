using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class StrokeShape
    {
        [Tooltip("Stroke length at full drive, as a fraction of the limb length; scales with the square root of the drive.")]
        public float length = 0.8f;
        [Tooltip("Where the power stroke starts, as a fraction of the stroke ahead of the rest point (0 = from rest).")]
        [Range(0f, 1f)] public float lead = 0.3f;
        [Header("Power: paw spread, wide path through the water")]
        [Tooltip("How far the paw swings out to the side and dips below its line, as fractions of the stroke.")]
        public float spread = 0.2f;
        public float dip = 0.15f;
        [Header("Recovery: folded, close to the body")]
        [Tooltip("How far the paw rises toward the surface and tucks in toward the body, as fractions of the stroke.")]
        public float lift = 0.35f;
        public float tuck = 0.25f;

        public StrokeShape() { }

        public StrokeShape(float length, float lead, float spread, float dip, float lift, float tuck)
        {
            this.length = length;
            this.lead = lead;
            this.spread = spread;
            this.dip = dip;
            this.lift = lift;
            this.tuck = tuck;
        }

        /// <summary>Paw offset from rest at a phase; power along <paramref name="power"/>, recovery back above and inside it.</summary>
        public Vector3 Offset(float phase, float powerFraction, Vector3 power, Vector3 outward, Vector3 up, float stroke)
        {
            float along, bulge;
            Vector3 side;
            if (phase < powerFraction)
            {
                float u = phase / powerFraction;
                along = u * u * (3f - 2f * u);
                bulge = Mathf.Sin(Mathf.PI * u);
                side = spread * outward - dip * up;
            }
            else
            {
                float u = (phase - powerFraction) / Mathf.Max(1e-4f, 1f - powerFraction);
                along = 1f - u * u * (3f - 2f * u);
                bulge = Mathf.Sin(Mathf.PI * u);
                side = lift * up - tuck * outward;
            }
            return stroke * ((along - lead) * power + bulge * side);
        }
    }

    /// <summary>
    /// Moves the limb IK targets along stroke paths from OtterLocomotionBrain's rhythm: phase, strength and direction
    /// per limb, so the limbs show what the allocation asks of them. Paths hang off the chest / hip bone, so they follow
    /// the spine's bend and roll; power strokes run horizontally, recoveries rise toward the surface.
    /// Runs after the spine and before the IK solve. While enabled it switches off the older limb drivers that write
    /// the same targets (HindPaddleDriverRB, IKStrokeController), and switches them back on when disabled.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class LimbStrokeAnimator : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        public StrokeShape hindStroke = new StrokeShape(0.8f, 0.3f, 0.2f, 0.15f, 0.35f, 0.25f);
        public StrokeShape foreStroke = new StrokeShape(0.7f, 0.1f, 0.15f, 0.1f, 0.3f, 0.3f);
        [Tooltip("Time constant of the written IK targets (s); hides the jump when a new stroke changes direction.")]
        public float targetSmoothing = 0.03f;
        public bool drawGizmos = true;

        private struct Limb
        {
            public bool valid;
            public Transform target, girdle;
            public Vector3 restLocal, forwardLocal, currentLocal;
            public float length;
        }

        private readonly Limb[] limbs = new Limb[OtterRig.LimbCount];
        private readonly List<Behaviour> suspended = new List<Behaviour>();
        private bool started;

        public bool HasLimbs => limbs.Any(l => l.valid);

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
        }

        // limb chains are captured on Start: when added while the scene loads, the IK components are not enabled yet
        private void Start()
        {
            Capture();
            started = true;
            SuspendOldDrivers();
        }

        private void OnEnable()
        {
            if (started) SuspendOldDrivers();
        }

        private void OnDisable() => ResumeOldDrivers();

        private void Capture()
        {
            var chains = OtterRig.FindLimbChains(transform);
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var ik = chains[i];
                if (ik == null || ik.target == null) { limbs[i] = default; continue; }
                var girdle = OtterRig.Girdle(ik.root.bone);
                Vector3 a = ik.root.bone.position, b = ik.mid.bone.position, c = ik.end.bone.position;
                Vector3 rest = girdle.InverseTransformPoint(ik.target.position);
                limbs[i] = new Limb
                {
                    valid = true,
                    target = ik.target,
                    girdle = girdle,
                    restLocal = rest,
                    currentLocal = rest,
                    forwardLocal = girdle.InverseTransformDirection(transform.forward),
                    length = Vector3.Distance(a, b) + Vector3.Distance(b, c),
                };
            }
            if (!HasLimbs) Debug.LogWarning($"[LimbStrokeAnimator] No limb IK chains with targets under '{name}'.", this);
        }

        private void SuspendOldDrivers()
        {
            var targets = new HashSet<Transform>(limbs.Where(l => l.valid).Select(l => l.target));
            IEnumerable<Behaviour> old =
                FindObjectsByType<HindPaddleDriverRB>(FindObjectsSortMode.None)
                    .Where(d => targets.Contains(d.leftLeg.ikTarget) || targets.Contains(d.rightLeg.ikTarget))
                    .Cast<Behaviour>()
                    .Concat(FindObjectsByType<IKStrokeController>(FindObjectsSortMode.None)
                        .Where(c => targets.Contains(c.leftLimbIkTarget) || targets.Contains(c.rightLimbIkTarget)));
            foreach (var b in old.Where(b => b.enabled))
            {
                b.enabled = false;
                suspended.Add(b);
            }
        }

        private void ResumeOldDrivers()
        {
            foreach (var b in suspended.Where(b => b != null)) b.enabled = true;
            suspended.Clear();
        }

        private void LateUpdate()
        {
            if (brain == null || !brain.HasLimbGeometry) return;
            // the rhythm steps with physics; run its phase on to this frame
            float ahead = Time.time - Time.fixedTime;
            float blend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, targetSmoothing));
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid) continue;
                Vector3 goal = l.restLocal + l.girdle.InverseTransformVector(StrokeOffset(i, l, ahead));
                l.currentLocal = Vector3.Lerp(l.currentLocal, goal, blend);
                l.target.position = l.girdle.TransformPoint(l.currentLocal);
            }
        }

        /// <summary>World-space paw offset from rest for limb i at the rhythm's phase, <paramref name="ahead"/> seconds on.</summary>
        private Vector3 StrokeOffset(int i, in Limb l, float ahead, float? phaseOverride = null)
        {
            var st = brain.Rhythm.limbs[i];
            if (st.drive < 1e-3f) return Vector3.zero;
            var shape = OtterRig.IsFore((LimbId)i) ? foreStroke : hindStroke;
            Vector3 up = Vector3.up;
            Vector3 forward = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.forwardLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, forward);
            Vector3 power = (forward * st.direction.x + right * st.direction.y).normalized;
            Vector3 rest = l.girdle.TransformPoint(l.restLocal);
            Vector3 outward = right * (Vector3.Dot(rest - l.girdle.position, right) >= 0f ? 1f : -1f);
            float phase = phaseOverride ?? (st.active ? Mathf.Repeat(st.phase + st.rate * ahead, 1f) : 0f);
            float stroke = l.length * shape.length * Mathf.Sqrt(st.drive);
            return shape.Offset(phase, st.powerFraction, power, outward, up, stroke);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!drawGizmos || !Application.isPlaying || brain == null || !brain.HasLimbGeometry) return;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var l = limbs[i];
                if (!l.valid) continue;
                var st = brain.Rhythm.limbs[i];
                Color c = OtterRig.IsFore((LimbId)i) ? new Color(0.3f, 0.8f, 1f) : new Color(1f, 0.6f, 0.2f);
                Vector3 rest = l.girdle.TransformPoint(l.restLocal);
                Gizmos.color = new Color(c.r, c.g, c.b, 0.5f);
                Gizmos.DrawWireSphere(rest, 0.006f);
                if (st.drive < 1e-3f) continue;
                const int n = 32;
                Vector3 prev = rest + StrokeOffset(i, l, 0f, 0f);
                for (int k = 1; k <= n; k++)
                {
                    float ph = k / (float)n;
                    Vector3 p = rest + StrokeOffset(i, l, 0f, ph);
                    Gizmos.color = ph <= st.powerFraction ? c : new Color(c.r, c.g, c.b, 0.35f);
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
                Gizmos.color = st.InPower ? Color.white : c;
                Gizmos.DrawSphere(l.target.position, st.InPower ? 0.009f : 0.006f);
            }
        }
#endif
    }
}
