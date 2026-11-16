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
    /// direction, its width tilted out of the horizontal, swept along the stroke on the outer side (power) and back on
    /// the inner side (recovery). The kick follows the allocated stroke: backward to push, forward to brake, leaning
    /// out or in as the stroke sculls. In time it is a kick and glide: the push snaps off fast and eases as the leg
    /// straightens, the foot holds out behind for a moment, then draws forward to cock for the next push.
    /// </summary>
    [Serializable]
    public class KickEllipse
    {
        [Tooltip("Half the kick's length along its direction and half its width across (m).")]
        public float forwardRadius = 0.05f;
        public float sideRadius = 0.01f;
        [Tooltip("Size at the lightest drive that kicks and at full drive, as multiples of the radii.")]
        public float lightScale = 1f;
        public float fullScale = 1.9f;
        [Header("Timing")]
        [Tooltip("How sharp the push is: it peaks 1/snap of the way through the power stroke and eases out as the leg straightens (2 = an even surge).")]
        public float snap = 4f;
        [Tooltip("Share of the recovery the foot stays out behind (the glide) before drawing forward.")]
        [Range(0f, 0.8f)] public float glide = 0.35f;
        [Tooltip("How far back along the return the foot drifts during the glide, as a fraction of the return.")]
        [Range(0f, 0.5f)] public float glideDrift = 0.12f;
        [Tooltip("Share of the recovery the foot holds cocked at the front of the loop, while the flipper turns square for the next push.")]
        [Range(0f, 0.4f)] public float cock = 0.12f;

        /// <summary>Whether a phase is in the cocked hold at the end of the recovery.</summary>
        public bool Cocked(float phase, float powerFraction) => phase >= powerFraction + (1f - powerFraction) * (1f - cock);
        [Tooltip("Where the foot rests on the loop: degrees from the front end, 90 = the outer side.")]
        [Range(0f, 360f)] public float restAngleDeg = 285f;
        [Tooltip("Tilts the loop's width about the kick direction, mirrored per leg (negative = the power side goes deeper).")]
        [Range(-90f, 90f)] public float trajectoryTiltDeg = -45.1f;
        [Header("Direction")]
        [Tooltip("Exaggerates how far the kick leans out of the fore-aft line, relative to the allocated stroke's lean, so the two legs' different strokes read.")]
        public float leanGain = 2f;
        public float maxLeanDeg = 70f;
        [Tooltip("How far toward the midline a foot may swing, as a fraction of its rest distance from it; held off softly so the feet never cross.")]
        [Range(0f, 1f)] public float midlineReach = 0.6f;
        [Header("Turning while prone: the feet push flat out, so the allocation keeps them nearly straight")]
        [Tooltip("How far the back end of the kick swings outward in a full turn (deg), times each leg's amplifier; fades out on the back.")]
        public float turnKickYawDeg = 30.3f;
        public float innerYawAmplifier = 1.5f;
        public float outerYawAmplifier = 0f;
        [Tooltip("Turn rate demand (deg/s) that counts as a full turn.")]
        public float fullTurnRate = 120f;

        public float Scale(float drive) => Mathf.Lerp(lightScale, fullScale, drive);

        /// <summary>Foot offset from rest at a phase. kickForward points at the loop's front end; outward is across it, toward the leg's side.</summary>
        public Vector3 Offset(float phase, float powerFraction, Vector3 kickForward, Vector3 outward, float scale)
        {
            float along;
            if (phase < powerFraction)
            {
                float u = phase / powerFraction, k = Mathf.Max(2f, snap);
                along = 1f - Mathf.Pow(1f - u, k) * (1f + k * u);
            }
            else
            {
                float u = (phase - powerFraction) / Mathf.Max(1e-4f, 1f - powerFraction), drawEnd = 1f - cock;
                along = 1f + (u < glide
                    ? glideDrift * Smooth(u / glide)
                    : glideDrift + (1f - glideDrift) * Smooth(Mathf.Clamp01((u - glide) / Mathf.Max(1e-4f, drawEnd - glide))));
            }
            return scale * (Point(Mathf.PI * along, kickForward, outward) - Point(restAngleDeg * Mathf.Deg2Rad, kickForward, outward));
        }

        private Vector3 Point(float theta, Vector3 forward, Vector3 outward) =>
            forward * (Mathf.Cos(theta) * forwardRadius) + outward * (Mathf.Sin(theta) * sideRadius);

        private static float Smooth(float u) => u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Turns the hind flippers at the ankle and toes, on top of the IK: through the push the web turns square to its sweep
    /// and spreads, so it shoves water; drawing forward it folds and turns edge-on so it slips through. The web is a flat
    /// fan, so either face can meet the sweep: each push holds the one the ankle could reach at the last push's fastest
    /// moment, since the leg swings the foot a long way through the stroke.
    /// </summary>
    [Serializable]
    public class FlipperPose
    {
        public bool enabled = true;
        [Tooltip("Most the web turns from its rest on the shin (deg) to square to the sweep, and to feather edge-on while drawing forward.")]
        public float maxSquareDeg = 120f;
        public float maxFeatherDeg = 60f;
        [Tooltip("Share of the turn taken at the ankle (the IK chain's end bone); the toes take the rest. Only when the IK leaves the end bone's rotation alone.")]
        [Range(0f, 1f)] public float ankleShare = 0.5f;
        [Tooltip("Most the fan twists in its own plane (deg) to keep its toes pointing the way they do at rest, out and back, so squaring up never swings them in toward the other foot.")]
        public float maxTwistDeg = 70f;
        [Tooltip("Feathering: above this speed (m/s, relative to the hip) the web lies along the way the leg swings it; slower, along the planned path this far ahead (fraction of the power stroke).")]
        public float followSpeed = 0.15f;
        public float lookAhead = 0.3f;
        [Tooltip("Time constants (s) of the flipper turning square while cocked for the push, and relaxing after it through the glide; and the fastest it turns (deg/s).")]
        public float turnTime = 0.02f;
        public float relaxTime = 0.08f;
        public float maxTurnSpeed = 1200f;
        [Tooltip("The flipper starts relaxing over this last fraction of the power stroke.")]
        public float release = 0.1f;
        [Tooltip("Web width while pushing and while drawing forward, as multiples of its rest width.")]
        public float spread = 1.2f;
        public float fold = 0.8f;
    }

    /// <summary>
    /// Moves the limb IK targets along stroke paths from OtterLocomotionBrain's rhythm: phase and strength per limb,
    /// so the limbs show what the allocation asks of them. Hind feet kick along HindPaddleTrajectoryRB's loop, aimed by
    /// the allocated force (and, prone, splayed by the turn the brain asks for); forepaws sweep along the allocated
    /// force direction, and rest folded on the chest when they have nothing to do, as on the back. Paths hang off the
    /// chest / hip bone, so they follow the spine's bend and roll. After the IK solve the hind flippers are turned at
    /// the ankle and toes (FlipperPose) to push square through the power stroke and slip edge-on through the recovery.
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
        public FlipperPose flipper = new FlipperPose();
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

            // hind flipper: the chain's end bone (the foot, hinged at the ankle) and the toe fan below it
            public AdvancedTwoBoneIK ik;
            public Transform end, web;
            public Quaternion endRest, webRest;
            /// <summary>Web normal in the flipper's space (the face that is dorsal at rest), and where it points now, in girdle space.</summary>
            public Vector3 normalLocal, aimLocal;
            /// <summary>The ankle's turn from its rest on the shin, and how far it would have to turn to meet its goal unclamped (deg).</summary>
            public float turned, need;
            /// <summary>Toe direction in the flipper's space and, at rest, in girdle space; and the fan's twist in its plane (deg).</summary>
            public Vector3 toesLocal, toesRestLocal;
            public float twist;
            /// <summary>Middle of the web in the flipper's space, and where the leg put it last frame (ankle at rest), in girdle space.</summary>
            public Vector3 centerLocal, lastCenter;
            public bool hasLastCenter;
            public Vector3 webScale;
            public int widthAxis;
            /// <summary>Planned foot motion along the path this frame: direction in girdle space and speed (m/s); sweep = the push's overall direction.</summary>
            public Vector3 motionLocal, sweepLocal;
            public float motionSpeed;
            /// <summary>1 while the flipper should push square (the power stroke, and cocked just before it), else 0.</summary>
            public float push;
            public float width;
            /// <summary>The face that meets this push (+1 = the dorsal face), the one that was nearer at the last push's fastest
            /// moment, and that push's sweep and peak speed.</summary>
            public float face, peakFace, peakSpeed;
            public Vector3 lastSweepLocal;
            public bool pushing, hasPeak;
        }

        private readonly Limb[] limbs = new Limb[OtterRig.LimbCount];
        private readonly List<Behaviour> suspended = new List<Behaviour>();
        private AfterLimbIK afterIK;
        private bool started;

        public bool HasLimbs => limbs.Any(l => l.valid);

        /// <summary>A hind flipper's ankle turn from rest (deg), whether it is set to push, its web width, and the turn its goal
        /// asks for (deg); zeros for a forepaw.</summary>
        public Vector4 FlipperState(LimbId limb)
        {
            var l = limbs[(int)limb];
            return l.web != null ? new Vector4(l.turned, l.push, l.width, l.need) : Vector4.zero;
        }

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
            afterIK = gameObject.AddComponent<AfterLimbIK>();
            afterIK.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            afterIK.Solved += PoseFlippers;
        }

        private void OnEnable()
        {
            if (started) SuspendOldDrivers();
        }

        private void OnDisable()
        {
            ResumeOldDrivers();
            RestoreFlippers();
        }

        private void OnDestroy()
        {
            if (afterIK != null) Destroy(afterIK);
        }

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
                if (!OtterRig.IsFore((LimbId)i)) CaptureFlipper(ref limbs[i], ik);
            }
            if (!HasLimbs) Debug.LogWarning($"[LimbStrokeAnimator] No limb IK chains with targets under '{name}'.", this);
        }

        // The web's normal from the skinned mesh, signed to the face that is dorsal with the leg trailing at rest. Its
        // width runs across the toes.
        private void CaptureFlipper(ref Limb l, AdvancedTwoBoneIK ik)
        {
            var end = ik.end.bone;
            var web = OtterRig.Flipper(end);
            if (web == end || web.childCount == 0 || !OtterRig.TryFootPlane(web, out var normal, out _, out var center, out _)) return;
            Vector3 n = web.TransformDirection(normal);
            if (Vector3.Dot(n, l.girdle.TransformDirection(l.upLocal)) < 0f) n = -n;
            Vector3 toes = Vector3.ProjectOnPlane(web.GetChild(0).position - web.position, n).normalized;
            Vector3 across = web.InverseTransformDirection(Vector3.Cross(n, toes));
            int axis = 0;
            for (int k = 1; k < 3; k++)
                if (Mathf.Abs(across[k]) > Mathf.Abs(across[axis])) axis = k;
            l.ik = ik;
            l.end = end;
            l.web = web;
            l.endRest = end.localRotation;
            l.webRest = web.localRotation;
            l.webScale = web.localScale;
            l.normalLocal = web.InverseTransformDirection(n);
            l.aimLocal = l.girdle.InverseTransformDirection(n);
            l.toesLocal = web.InverseTransformDirection(toes);
            l.toesRestLocal = l.girdle.InverseTransformDirection(toes);
            l.twist = 0f;
            l.centerLocal = center;
            l.hasLastCenter = false;
            l.widthAxis = axis;
            l.width = 1f;
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
                if (l.end != null) PlanFlipper(i, ref l, st, phase);
            }
        }

        // Which way the foot is about to move along its path, and whether it is pushing, for the flipper pass after the
        // IK. The flipper aims along a stretch of path ahead rather than the path's tangent, so it turns smoothly as the
        // loop curls; held cocked it already aims down the coming push.
        private void PlanFlipper(int i, ref Limb l, in LimbStroke st, float phase)
        {
            float pf = st.powerFraction, window = flipper.lookAhead * pf;
            bool cocked = hindKick.Cocked(phase, pf);
            float from = cocked ? 0f : phase;
            float to = from < pf ? Mathf.Min(from + window, pf) : from + window;
            if (from < pf && to - from < 0.5f * window) from = to - 0.5f * window;
            Vector3 travel = StrokeOffset(i, l, st, Mathf.Repeat(to, 1f)) - StrokeOffset(i, l, st, Mathf.Repeat(from, 1f));
            l.motionSpeed = st.active ? travel.magnitude * Mathf.Max(0f, st.rate) / Mathf.Max(1e-4f, to - from) : 0f;
            l.motionLocal = l.motionSpeed > 1e-4f ? l.girdle.InverseTransformDirection(travel.normalized) : Vector3.zero;
            KickFrame(l, st, out var kickForward, out _);
            l.sweepLocal = l.girdle.InverseTransformDirection(-kickForward);
            bool pushing = phase < st.powerFraction * (1f - flipper.release) || hindKick.Cocked(phase, st.powerFraction);
            l.push = st.active && !st.parking && l.size > 1e-4f && pushing ? 1f : 0f;
        }

        private void PoseFlippers()
        {
            if (!isActiveAndEnabled || brain == null || !brain.HasLimbGeometry) return;
            var f = flipper;
            float squaring = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.turnTime));
            float relaxing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.relaxTime));
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.web == null) continue;
                float turn = l.push > 0f ? squaring : relaxing;
                // the IK carries the foot rigidly on the shin unless it aligns the end bone itself
                bool ankle = l.ik != null && !l.ik.alignEndRotation;
                if (ankle) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                // how much of a kick the foot is making: none at rest, full from the lightest kick up
                float kicking = f.enabled ? Mathf.Clamp01(l.size / Mathf.Max(1e-3f, hindKick.lightScale)) : 0f;
                Vector3 rest = l.web.TransformDirection(l.normalLocal);
                // drawing forward the web lies along the way the leg actually swings it (it hangs on the shin well
                // beyond the IK's end); the planned path steers it while the foot barely moves
                Vector3 center = l.girdle.InverseTransformPoint(l.web.TransformPoint(l.centerLocal));
                Vector3 swung = l.hasLastCenter ? l.girdle.TransformVector(center - l.lastCenter) / Mathf.Max(1e-4f, Time.deltaTime) : Vector3.zero;
                l.lastCenter = center;
                l.hasLastCenter = true;
                bool swinging = swung.magnitude > f.followSpeed;
                Vector3 motion = swinging ? swung.normalized : l.girdle.TransformDirection(l.motionLocal);
                // where the web should face, within reach of its rest on the shin
                Vector3 aim = l.girdle.TransformDirection(l.aimLocal);
                Vector3 goal = rest;
                l.need = 0f;
                bool pushing = kicking > 0f && l.push > 0f;
                if (pushing)
                {
                    // cocked and through the push the web squares to the push's overall direction, so it is already set
                    // when the snap comes. The leg swings the foot a long way through the push, so one face is held for
                    // the whole stroke: the one the ankle could reach at the last push's fastest moment
                    Vector3 sweep = l.girdle.TransformDirection(l.sweepLocal);
                    if (!l.pushing)
                    {
                        if (l.hasPeak && Vector3.Dot(l.lastSweepLocal, l.sweepLocal) < 0f) l.hasPeak = false;
                        l.face = l.hasPeak ? l.peakFace : Vector3.Dot(rest, sweep) >= 0f ? 1f : -1f;
                        l.peakSpeed = 0f;
                        l.lastSweepLocal = l.sweepLocal;
                    }
                    if (swung.magnitude > l.peakSpeed)
                    {
                        l.peakSpeed = swung.magnitude;
                        l.peakFace = Vector3.Dot(rest, sweep) >= 0f ? 1f : -1f;
                        l.hasPeak = true;
                    }
                    l.need = Vector3.Angle(rest, sweep * l.face);
                    goal = Vector3.RotateTowards(rest, sweep * l.face, f.maxSquareDeg * Mathf.Deg2Rad, 0f);
                }
                else if (kicking > 0f && (swinging || l.motionSpeed > 0.05f))
                {
                    // feather: the least turn that lays the web along its motion
                    Vector3 edge = Vector3.ProjectOnPlane(rest, motion);
                    if (edge.sqrMagnitude > 1e-4f)
                    {
                        l.need = Vector3.Angle(rest, edge);
                        goal = Vector3.RotateTowards(rest, edge.normalized, f.maxFeatherDeg * Mathf.Deg2Rad, 0f);
                    }
                }
                l.pushing = pushing;
                goal = Vector3.Slerp(rest, goal, kicking);
                // the aim eases toward the goal in the hip's space, and the ankle is solved fresh from the shin each
                // frame, so the web holds its line while the shin swings under it
                Vector3 next = Vector3.RotateTowards(aim, Vector3.Slerp(aim, goal, turn), f.maxTurnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
                next = Vector3.RotateTowards(rest, next, Mathf.Min(Vector3.Angle(rest, next), f.maxSquareDeg + 15f) * Mathf.Deg2Rad, 0f);
                l.aimLocal = l.girdle.InverseTransformDirection(next);
                l.turned = Vector3.Angle(rest, next);
                // the ankle takes its share of the turn and the toes the rest, so neither joint folds over alone
                Quaternion whole = Quaternion.FromToRotation(rest, next);
                if (ankle) l.end.rotation = Quaternion.Slerp(Quaternion.identity, whole, f.ankleShare) * l.end.rotation;
                l.web.rotation = Quaternion.FromToRotation(l.web.TransformDirection(l.normalLocal), next) * l.web.rotation;
                // the least turn can swing the toes anywhere in the new plane; twist them back toward where they point at rest
                Vector3 toesNow = l.web.TransformDirection(l.toesLocal);
                Vector3 toesWant = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.toesRestLocal), next);
                float twistGoal = toesWant.sqrMagnitude > 1e-4f
                    ? Mathf.Clamp(Vector3.SignedAngle(toesNow, toesWant, next), -f.maxTwistDeg, f.maxTwistDeg) * kicking
                    : l.twist;
                l.twist = Mathf.MoveTowards(l.twist, Mathf.Lerp(l.twist, twistGoal, turn), f.maxTurnSpeed * Time.deltaTime);
                l.web.rotation = Quaternion.AngleAxis(l.twist, next) * l.web.rotation;

                l.width = Mathf.Lerp(l.width, Mathf.Lerp(1f, Mathf.Lerp(f.fold, f.spread, l.push), kicking), turn);
                var scale = l.webScale;
                scale[l.widthAxis] *= l.width;
                l.web.localScale = scale;
            }
        }

        private void RestoreFlippers()
        {
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.web == null) continue;
                if (l.ik != null && !l.ik.alignEndRotation) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                l.web.localScale = l.webScale;
                l.aimLocal = l.girdle.InverseTransformDirection(l.web.TransformDirection(l.normalLocal));
                l.turned = 0f;
                l.twist = 0f;
                l.width = 1f;
                l.hasLastCenter = false;
                l.pushing = false;
                l.hasPeak = false;
            }
        }

        private float TargetSize(int i, in Limb l, in LimbStroke st)
        {
            // a limb with nothing left to do shrinks back to rest while its cycle runs out, e.g. forepaws folding as the otter rolls onto its back
            if (!st.active || st.parking) return 0f;
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

            KickFrame(l, st, out var kickForward, out var outward);
            Vector3 offset = hindKick.Offset(phase, st.powerFraction, kickForward, outward, l.size);
            float reach = Mathf.Abs(Vector3.Dot(rest - l.girdle.position, right)) * hindKick.midlineReach;
            float inward = -Vector3.Dot(offset, right * side);
            if (inward > 0f && reach > 1e-4f) offset += right * side * (inward - reach * (1f - Mathf.Exp(-inward / reach)));
            return offset;
        }

        /// <summary>A hind kick's loop axes (world): kickForward points at the loop's front end, outward across it toward the leg's side.</summary>
        private void KickFrame(in Limb l, in LimbStroke st, out Vector3 kickForward, out Vector3 outward)
        {
            var k = hindKick;
            Vector3 up = Vector3.up;
            Vector3 forward = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.forwardLocal), up).normalized;
            Vector3 right = Vector3.Cross(up, forward);
            float side = Vector3.Dot(l.girdle.TransformPoint(l.restLocal) - l.girdle.position, right) >= 0f ? 1f : -1f;
            // + lean = the foot sweeps out away from the midline
            Vector2 d = st.direction;
            float back = d.x <= 0f ? 1f : -1f;
            float lean = Mathf.Clamp(Mathf.Atan2(d.y * side, Mathf.Abs(d.x)) * Mathf.Rad2Deg * k.leanGain, -k.maxLeanDeg, k.maxLeanDeg);
            float turnRate = brain.Demand.yawRateTarget;
            float turn01 = Mathf.Clamp01(Mathf.Abs(turnRate) / Mathf.Max(1f, k.fullTurnRate));
            bool inner = Mathf.Sign(turnRate) == side;
            lean += k.turnKickYawDeg * turn01 * (inner ? k.innerYawAmplifier : k.outerYawAmplifier) * (1f - brain.Posture.SupineFraction);
            // the loop's long axis, front end forward; a braking kick runs it back to front
            Vector3 axis = Quaternion.AngleAxis(-lean * side * back, up) * forward;
            // the loop's tilt follows the body: on the back the power side goes toward the belly, now facing up
            float bodyUp = Vector3.Dot(l.girdle.TransformDirection(l.upLocal), up) >= 0f ? 1f : -1f;
            outward = Quaternion.AngleAxis(k.trajectoryTiltDeg * side * bodyUp, axis) * (Vector3.Cross(up, axis) * side);
            kickForward = axis * back;
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
