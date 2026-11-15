using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>Forepaw stroke: a sweep along the allocated direction, spread out and down, folded back in and up.</summary>
    [Serializable]
    public class StrokeShape
    {
        [Tooltip("Stroke length as a fraction of the limb length, at full drive and at the lightest drive that strokes.")]
        public float length = 1.3f;
        public float lightLength = 0.6f;
        [Tooltip("Where the power stroke starts, as a fraction of the stroke ahead of the rest point (0 = from rest).")]
        [Range(0f, 1f)] public float lead = 0.1f;
        [Header("Power: paw spread, wide path through the water")]
        [Tooltip("How far the paw swings out to the side and dips below its line, as fractions of the stroke.")]
        public float spread = 0.15f;
        public float dip = 0.1f;
        [Header("Recovery: folded, close to the body")]
        [Tooltip("How far the paw rises toward the surface and tucks in toward the body, as fractions of the stroke.")]
        public float lift = 0.3f;
        public float tuck = 0.3f;

        /// <summary>Stroke length (m) of a limb at a drive.</summary>
        public float Length(float limbLength, float drive) => limbLength * Mathf.Lerp(lightLength, length, drive);

        /// <summary>Paw offset from rest at a phase; power along <paramref name="power"/>, recovery back above and inside it.</summary>
        public Vector3 Offset(float phase, float powerFraction, Vector3 power, Vector3 outward, Vector3 up, float stroke)
        {
            float along, bulge;
            Vector3 side;
            if (phase < powerFraction)
            {
                float u = phase / powerFraction;
                along = Smooth(u);
                bulge = Mathf.Sin(Mathf.PI * u);
                side = spread * outward - dip * up;
            }
            else
            {
                float u = (phase - powerFraction) / Mathf.Max(1e-4f, 1f - powerFraction);
                along = 1f - Smooth(u);
                bulge = Mathf.Sin(Mathf.PI * u);
                side = lift * up - tuck * outward;
            }
            return stroke * ((along - lead) * power + bulge * side);
        }

        private static float Smooth(float u) => u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Hind foot kick, the loop of HindPaddleTrajectoryRB with its tuning: a long thin ellipse along the kick
    /// direction, its width tilted out of the horizontal, swept backward on the outer side (power) and forward on the
    /// inner side (recovery). In a turn the back end of each leg's loop swings outward, the inner leg's most.
    /// </summary>
    [Serializable]
    public class KickEllipse
    {
        [Tooltip("Half the kick's length along its direction and half its width across (m).")]
        public float forwardRadius = 0.05f;
        public float sideRadius = 0.01f;
        [Tooltip("Size at the lightest drive that kicks and at full drive, as multiples of the radii.")]
        public float lightScale = 0.5f;
        public float fullScale = 1.8f;
        [Tooltip("Where the foot rests on the loop: degrees from the front end, 90 = the outer side.")]
        [Range(0f, 360f)] public float restAngleDeg = 285f;
        [Tooltip("Tilts the loop's width about the kick direction, mirrored per leg (negative = the power side goes deeper).")]
        [Range(-90f, 90f)] public float trajectoryTiltDeg = -45.1f;
        [Header("Turning")]
        [Tooltip("How far the back end of the kick swings outward in a full turn (deg), times each leg's amplifier.")]
        public float turnKickYawDeg = 30.3f;
        public float innerYawAmplifier = 1.5f;
        public float outerYawAmplifier = 0f;
        [Tooltip("Turn rate demand (deg/s) that counts as a full turn.")]
        public float fullTurnRate = 120f;

        public float Scale(float drive) => Mathf.Lerp(lightScale, fullScale, drive);

        /// <summary>Foot offset from rest at a phase. kickForward points at the loop's front end; outward is across it, toward the leg's side.</summary>
        public Vector3 Offset(float phase, float powerFraction, Vector3 kickForward, Vector3 outward, float scale)
        {
            float theta = phase < powerFraction
                ? Mathf.PI * Smooth(phase / powerFraction)
                : Mathf.PI * (1f + Smooth((phase - powerFraction) / Mathf.Max(1e-4f, 1f - powerFraction)));
            return scale * (Point(theta, kickForward, outward) - Point(restAngleDeg * Mathf.Deg2Rad, kickForward, outward));
        }

        private Vector3 Point(float theta, Vector3 forward, Vector3 outward) =>
            forward * (Mathf.Cos(theta) * forwardRadius) + outward * (Mathf.Sin(theta) * sideRadius);

        private static float Smooth(float u) => u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Moves the limb IK targets along stroke paths from OtterLocomotionBrain's rhythm: phase and strength per limb,
    /// so the limbs show what the allocation asks of them. Hind feet kick along HindPaddleTrajectoryRB's loop, splayed
    /// by the turn the brain asks for; forepaws sweep along the allocated force direction. Paths hang off the chest /
    /// hip bone, so they follow the spine's bend and roll.
    /// Runs after the spine and before the IK solve. While enabled it switches off the older limb drivers that write
    /// the same targets (HindPaddleDriverRB, IKStrokeController), and switches them back on when disabled.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class LimbStrokeAnimator : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        public KickEllipse hindKick = new KickEllipse();
        public StrokeShape foreStroke = new StrokeShape();
        [Tooltip("Time constant of a stroke growing in when a limb starts and fading out when it rests (s).")]
        public float sizeTime = 0.1f;
        [Tooltip("Time constant of the written IK targets (s); hides the jump when a new stroke changes direction.")]
        public float targetSmoothing = 0.03f;
        public bool drawGizmos = true;

        private struct Limb
        {
            public bool valid;
            public Transform target, girdle;
            public Vector3 restLocal, forwardLocal, upLocal, currentLocal;
            public float length;
            /// <summary>Current stroke size: metres for a forepaw, a multiple of the kick radii for a hind foot.</summary>
            public float size;
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
                    upLocal = girdle.InverseTransformDirection(transform.up),
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
            float grow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, sizeTime));
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid) continue;
                var st = brain.Rhythm.limbs[i];
                l.size = Mathf.Lerp(l.size, TargetSize(i, l, st), grow);
                float phase = st.active ? Mathf.Repeat(st.phase + st.rate * ahead, 1f) : 0f;
                Vector3 goal = l.restLocal + l.girdle.InverseTransformVector(StrokeOffset(i, l, st, phase));
                l.currentLocal = Vector3.Lerp(l.currentLocal, goal, blend);
                l.target.position = l.girdle.TransformPoint(l.currentLocal);
            }
        }

        private float TargetSize(int i, in Limb l, in LimbStroke st)
        {
            if (!st.active) return 0f;
            return OtterRig.IsFore((LimbId)i) ? foreStroke.Length(l.length, st.drive) : hindKick.Scale(st.drive);
        }

        /// <summary>World-space paw offset from rest for limb i at a phase of its stroke, at the limb's current size.</summary>
        private Vector3 StrokeOffset(int i, in Limb l, in LimbStroke st, float phase)
        {
            if (l.size < 1e-5f) return Vector3.zero;
            Vector3 up = Vector3.up;
            Vector3 forward = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.forwardLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, forward);
            Vector3 rest = l.girdle.TransformPoint(l.restLocal);
            float side = Vector3.Dot(rest - l.girdle.position, right) >= 0f ? 1f : -1f;
            if (OtterRig.IsFore((LimbId)i))
            {
                Vector3 power = (forward * st.direction.x + right * st.direction.y).normalized;
                return foreStroke.Offset(phase, st.powerFraction, power, right * side, up, l.size);
            }

            var k = hindKick;
            float turnRate = brain.Demand.yawRateTarget;
            float turn01 = Mathf.Clamp01(Mathf.Abs(turnRate) / Mathf.Max(1f, k.fullTurnRate));
            bool inner = Mathf.Sign(turnRate) == side;
            float yaw = k.turnKickYawDeg * turn01 * (inner ? k.innerYawAmplifier : k.outerYawAmplifier);
            Vector3 kickForward = Quaternion.AngleAxis(-yaw * side, up) * forward;
            // the loop's tilt follows the body: on the back the power side goes toward the belly, now facing up
            float bodyUp = Vector3.Dot(l.girdle.TransformDirection(l.upLocal), up) >= 0f ? 1f : -1f;
            Vector3 outward = Quaternion.AngleAxis(k.trajectoryTiltDeg * side * bodyUp, kickForward) * (Vector3.Cross(up, kickForward) * side);
            return k.Offset(phase, st.powerFraction, kickForward, outward, l.size);
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
                if (l.size < 1e-5f) continue;
                const int n = 32;
                Vector3 prev = rest + StrokeOffset(i, l, st, 0f);
                for (int k = 1; k <= n; k++)
                {
                    float ph = k / (float)n;
                    Vector3 p = rest + StrokeOffset(i, l, st, ph);
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
