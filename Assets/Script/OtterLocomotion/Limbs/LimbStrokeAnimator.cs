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
    /// Hind foot kick, drawn in and kicked out like pedalling. In the hip's frame the ankle runs a closed loop around the
    /// hip joint between a tucked pose (knee bent, foot drawn up toward the belly) and a stretched one (leg straight out
    /// behind). The push sweeps it back along the far side of the loop, the leg straightening early, a little out from
    /// the body; the draw brings it forward along the near side, the knee bending first so the foot comes in close before
    /// it swings up, a little in toward the midline. Lighter kicks run a smaller loop about the same middle; a braking
    /// kick runs the loop backward. The loop rides on the hip bone, so it pitches and rolls with the spine. In time it is
    /// a kick and glide: the push snaps off fast and eases as the leg straightens, the foot holds out behind for a moment,
    /// then draws in to cock for the next push.
    /// </summary>
    [Serializable]
    public class HindKick
    {
        [Header("Shape: ankle from the hip joint, in leg lengths; angles from straight back toward the belly")]
        [Tooltip("Tucked pose at the start of the push: hip joint to ankle over the leg's length, and its angle (deg).")]
        public float tuckedReach = 0.58f;
        public float tuckedAngle = 72f;
        [Tooltip("Stretched pose at the end of the push.")]
        public float stretchedReach = 0.97f;
        public float stretchedAngle = 18f;
        [Tooltip("Out from the hip joint (leg lengths) when tucked, the knee opening outward, and when stretched, the foot in beside the tail.")]
        public float tuckedOut = 0.28f;
        public float stretchedOut = 0.08f;
        [Tooltip("How far the push sweeps out wide of the straight line from tucked to stretched (leg lengths).")]
        public float pushBow = 0.25f;
        [Tooltip("How far the knee folds the foot in toward the hip midway through the draw, as a fraction of its distance.")]
        [Range(0f, 0.6f)] public float drawFold = 0.35f;
        [Tooltip("Loop size at the lightest drive that kicks, as a fraction of the full loop.")]
        [Range(0.1f, 1f)] public float lightAmplitude = 0.75f;
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
        [Header("Limits")]
        [Tooltip("How far toward the midline a foot may swing, as a fraction of its rest distance from it; held off softly so the feet never cross.")]
        [Range(0f, 1f)] public float midlineReach = 0.6f;

        /// <summary>Loop size at a drive, as a fraction of the full loop.</summary>
        public float Amplitude(float drive) => Mathf.Lerp(lightAmplitude, 1f, drive);

        /// <summary>How far a kick growing in from rest (size 0) or fading out to it has moved onto its loop.</summary>
        public float Engagement(float size) => Mathf.Clamp01(size / lightAmplitude);

        /// <summary>Where on the loop the foot is at a phase: 0 = tucked, 0.5 = stretched, the push between, the draw after.</summary>
        public float LoopAt(float phase, float powerFraction)
        {
            if (phase < powerFraction)
            {
                float u = phase / powerFraction, k = Mathf.Max(2f, snap);
                return 0.5f * (1f - Mathf.Pow(1f - u, k) * (1f + k * u));
            }
            float w = (phase - powerFraction) / Mathf.Max(1e-4f, 1f - powerFraction), drawEnd = 1f - cock;
            float drawn = w < glide
                ? glideDrift * Smooth(w / glide)
                : glideDrift + (1f - glideDrift) * Smooth(Mathf.Clamp01((w - glide) / Mathf.Max(1e-4f, drawEnd - glide)));
            return 0.5f + 0.5f * drawn;
        }

        /// <summary>Ankle at loop position s on a loop of the given size, in leg lengths from the hip joint: x back along
        /// the body, y out toward the leg's side, z toward the belly.</summary>
        public Vector3 Point(float s, float amplitude)
        {
            // the two poses for this loop size, shrunk toward each other about their middle
            float midAngle = 0.5f * (tuckedAngle + stretchedAngle), midReach = 0.5f * (tuckedReach + stretchedReach);
            Vector2 tuck = Polar(midAngle + amplitude * (tuckedAngle - midAngle), midReach + amplitude * (tuckedReach - midReach));
            Vector2 stretch = Polar(midAngle + amplitude * (stretchedAngle - midAngle), midReach + amplitude * (stretchedReach - midReach));
            float midOut = 0.5f * (tuckedOut + stretchedOut);
            float tuckOut = midOut + amplitude * (tuckedOut - midOut), stretchOut = midOut + amplitude * (stretchedOut - midOut);
            s = Mathf.Repeat(s, 1f);
            // seen from the side (back, belly); the push bows out sideways and the draw folds in toward the hip, so the
            // loop is not flat
            Vector2 profile;
            float outward;
            if (s <= 0.5f)
            {
                // push: the leg straightens straight back from tucked to stretched, sweeping wide of the body
                float w = 2f * s;
                profile = Vector2.Lerp(tuck, stretch, w);
                outward = Mathf.Lerp(tuckOut, stretchOut, w) + amplitude * pushBow * Mathf.Sin(Mathf.PI * w);
            }
            else
            {
                // draw: the knee folds, pulling the foot in toward the hip as it comes forward close to the body
                float w = 2f * s - 1f;
                profile = Vector2.Lerp(stretch, tuck, w) * (1f - amplitude * drawFold * Mathf.Sin(Mathf.PI * w));
                outward = Mathf.Lerp(stretchOut, tuckOut, w);
            }
            return new Vector3(profile.x, outward, profile.y);
        }

        // back along the body and toward the belly, at an angle (deg) from straight back
        private static Vector2 Polar(float angle, float reach) =>
            new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * reach;

        private static float Smooth(float u) => u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Turning, the hind feet kick in different directions: each kick loop swings about the hip's dorso-ventral axis,
    /// the foot on the outside of the turn kicking out wide and the inside one in toward the tail. Turning on the spot,
    /// the inside foot's kick swings round toward the middle of the turn, sculling the hindquarters around. A braking
    /// kick runs its loop backward, so it swings the other way to push the same way round.
    /// </summary>
    [Serializable]
    public class HindTurn
    {
        [Tooltip("Yaw rate target (deg/s) that counts as a full turn.")]
        public float fullTurnRate = 90f;
        [Tooltip("Speed target (m/s) from which the otter swims through its turns rather than turning on the spot.")]
        public float travelSpeed = 0.5f;
        [Tooltip("How far the outside foot's kick swings out, and the inside one's in, at a full turn (deg).")]
        public float outerYaw = 45f;
        public float innerYaw = 15f;
        [Tooltip("Turning on the spot, how far round toward the middle of the turn (90 deg out) the inside foot's kick swings at a full turn.")]
        [Range(0f, 1f)] public float innerSteer = 0.75f;
        [Tooltip("Most a braking kick swings (deg); swung far in, its stretched end would cross under the other leg.")]
        public float maxBrakeYaw = 40f;
        [Tooltip("Time constant of the kicks following the turn (s).")]
        public float response = 0.15f;
    }

    /// <summary>
    /// Sprinting prone the hind feet stop kicking: the legs stretch out behind along the hips, feet in beside the tail
    /// and webs flat, and heave up and down with the spine as one fluke, the feet running behind the hips like the end
    /// of a whip and the webs pitched into the water they meet. The wave keeps the hind rhythm's beat; FlukeSpineWave
    /// puts it on the spine.
    /// </summary>
    [Serializable]
    public class FlukeStroke
    {
        public bool enabled = true;
        [Tooltip("Time constant of changing between kicking and the fluke (s).")]
        public float blendTime = 0.3f;
        [Header("Legs held out behind, from the hip joint")]
        [Tooltip("Ankle distance over the leg's length, and its angle toward the belly from straight back (deg).")]
        public float reach = 0.96f;
        public float angle = 8f;
        [Tooltip("How far out the feet sit (leg lengths; negative = in toward the tail).")]
        public float outward = -0.15f;
        [Header("Wave")]
        [Tooltip("How far the legs swing up and down about the hips on top of the hips' own pitch (deg), and how far behind the spine wave's beat they run (cycles).")]
        public float sweep = 12f;
        [Range(0f, 1f)] public float lag = 0.75f;
        [Tooltip("Web pitch into the water it meets (deg): its trailing edge rises as it sweeps down and drops as it sweeps up.")]
        public float attack = 20f;
        [Tooltip("Web width as a multiple of its rest width.")]
        public float width = 1.15f;
        [Tooltip("Spine pitch asked for at full drive (deg; the hips 1.5 times this, the chest 0.4), and how far the wave runs behind from chest to hips (rad per joint). The harmonizer's chain stiffness and damping take off about three quarters.")]
        public float spineAmplitude = 35f;
        public float spineWaveOffset = 0.8f;
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
    /// so the limbs show what the allocation asks of them. Hind feet draw in and kick out on a loop about the hip joint
    /// (HindKick), run backward to brake, part ways in a turn (HindTurn), and sprinting prone hold out behind as a
    /// fluke (FlukeStroke); forepaws sweep along the allocated
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
        public HindKick hindKick = new HindKick();
        public HindTurn hindTurn = new HindTurn();
        public FlukeStroke fluke = new FlukeStroke();
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
            /// <summary>The chain's root joint (shoulder / hip joint) in girdle space.</summary>
            public Vector3 jointLocal;
            public float length;
            /// <summary>Current stroke size: metres for a forepaw, a fraction of the full kick loop for a hind foot.</summary>
            public float size;
            /// <summary>Hind foot: how far its kick loop swings about the hip's dorso-ventral axis (deg, + = out).</summary>
            public float yaw;
            public AdvancedTwoBoneIK ik;
            /// <summary>Whether the scene limits the chain's mid joint (knee / elbow) about its rest pose.</summary>
            public bool kneeLimited;

            // hind flipper: the chain's end bone (the foot, hinged at the ankle) and the toe fan below it
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
        /// <summary>The turn the hind kicks follow, -1..1 of a full turn, + = right.</summary>
        private float turn;

        public bool HasLimbs => limbs.Any(l => l.valid);

        /// <summary>How far a hind foot's kick loop swings out for the turn (deg); 0 for a forepaw.</summary>
        public float KickYaw(LimbId limb) => limbs[(int)limb].yaw;

        /// <summary>How far the hind legs have gone over from kicking to the fluke (0..1).</summary>
        public float Fluke { get; private set; }
        /// <summary>The fluke's beat (cycles; the hind rhythm's phase) and strength (the hind drive, 0..1).</summary>
        public float FlukePhase { get; private set; }
        public float FlukeDrive { get; private set; }

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
            FreeKnees(true);
            afterIK = gameObject.AddComponent<AfterLimbIK>();
            afterIK.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            afterIK.Solved += PoseFlippers;
        }

        private void OnEnable()
        {
            if (!started) return;
            SuspendOldDrivers();
            FreeKnees(true);
        }

        private void OnDisable()
        {
            ResumeOldDrivers();
            RestoreFlippers();
            FreeKnees(false);
        }

        // The hind knee's limits are set about the rig's nearly straight rest pose, which stops it short of the tuck the
        // draw folds it into; the IK's pole keeps it bending the right way.
        private void FreeKnees(bool free)
        {
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (l.valid && l.ik != null && l.kneeLimited && !OtterRig.IsFore((LimbId)i)) l.ik.mid.useEulerLimits = !free;
            }
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
                    jointLocal = girdle.InverseTransformPoint(a),
                    length = Vector3.Distance(a, b) + Vector3.Distance(b, c),
                    ik = ik,
                    kneeLimited = ik.mid.useEulerLimits,
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

        // The fluke is set before the spine's pass, which takes its beat from here
        private void Update()
        {
            if (brain == null || !brain.HasLimbGeometry) return;
            float goal = fluke.enabled && brain.Rhythm.HindTogether ? 1f - brain.Posture.SupineFraction : 0f;
            Fluke = Mathf.Lerp(Fluke, goal, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, fluke.blendTime)));
            // the hind feet keep one beat when they kick together; the left one's unless it is resting
            var a = brain.Rhythm.limbs[(int)LimbId.HL];
            var b = brain.Rhythm.limbs[(int)LimbId.HR];
            var lead = a.active ? a : b;
            float drive = Mathf.Max(a.active && !a.parking ? a.drive : 0f, b.active && !b.parking ? b.drive : 0f);
            FlukeDrive = Mathf.Lerp(FlukeDrive, drive, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, sizeTime)));
            if (lead.active) FlukePhase = Mathf.Repeat(lead.phase + lead.rate * (Time.time - Time.fixedTime), 1f);
        }

        private void LateUpdate()
        {
            if (brain == null || !brain.HasLimbGeometry) return;
            // the rhythm steps with physics; run its phase on to this frame
            float ahead = Time.time - Time.fixedTime;
            float blend = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, targetSmoothing));
            float grow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, sizeTime));
            var demand = brain.Demand;
            float turnGoal = Mathf.Clamp(demand.yawRateTarget / Mathf.Max(1f, hindTurn.fullTurnRate), -1f, 1f);
            turn = Mathf.Lerp(turn, turnGoal, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, hindTurn.response)));
            float travel = Mathf.Clamp01(demand.speedTarget / Mathf.Max(1e-3f, hindTurn.travelSpeed));
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid) continue;
                var st = brain.Rhythm.limbs[i];
                l.size = Mathf.Lerp(l.size, TargetSize(i, l, st), grow);
                if (!OtterRig.IsFore((LimbId)i)) l.yaw = KickYaw(l, st, travel);
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
            Vector3 push = StrokeOffset(i, l, st, pf) - StrokeOffset(i, l, st, 0f);
            if (push.sqrMagnitude > 1e-10f) l.sweepLocal = l.girdle.InverseTransformDirection(push.normalized);
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
                float turn = l.push > 0f || Fluke > 0.5f ? squaring : relaxing;
                // the IK carries the foot rigidly on the shin unless it aligns the end bone itself
                bool ankle = l.ik != null && !l.ik.alignEndRotation;
                if (ankle) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                // how much of a kick the foot is making: none at rest, full from the lightest kick up
                float kicking = f.enabled ? hindKick.Engagement(l.size) : 0f;
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
                if (Fluke > 1e-3f) goal = Vector3.Slerp(goal, FlukeNormal(l, rest), Fluke);
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
                // the least turn can swing the toes anywhere in the new plane; twist them back toward where they point at
                // rest, or straight back along the fluke
                Vector3 toesNow = l.web.TransformDirection(l.toesLocal);
                Vector3 toesHome = Vector3.Slerp(l.girdle.TransformDirection(l.toesRestLocal), -l.girdle.TransformDirection(l.forwardLocal), Fluke);
                Vector3 toesWant = Vector3.ProjectOnPlane(toesHome, next);
                float twistGoal = toesWant.sqrMagnitude > 1e-4f
                    ? Mathf.Clamp(Vector3.SignedAngle(toesNow, toesWant, next), -f.maxTwistDeg, f.maxTwistDeg) * Mathf.Max(kicking, Fluke)
                    : l.twist;
                l.twist = Mathf.MoveTowards(l.twist, Mathf.Lerp(l.twist, twistGoal, turn), f.maxTurnSpeed * Time.deltaTime);
                l.web.rotation = Quaternion.AngleAxis(l.twist, next) * l.web.rotation;

                float kickWidth = Mathf.Lerp(1f, Mathf.Lerp(f.fold, f.spread, l.push), kicking);
                l.width = Mathf.Lerp(l.width, Mathf.Lerp(kickWidth, fluke.width, Fluke), turn);
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
            return OtterRig.IsFore((LimbId)i) ? foreStroke.Length(l.length, st.drive) : hindKick.Amplitude(st.drive);
        }

        /// <summary>World-space paw offset from rest for limb i at a phase of its stroke, at the limb's current size.</summary>
        private Vector3 StrokeOffset(int i, in Limb l, in LimbStroke st, float phase)
        {
            bool fore = OtterRig.IsFore((LimbId)i);
            if (l.size < 1e-5f && (fore || Fluke < 1e-3f)) return Vector3.zero;
            Vector3 rest = l.girdle.TransformPoint(l.restLocal);
            if (fore)
            {
                Vector3 up = Vector3.up;
                Vector3 forward = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.forwardLocal), up).normalized;
                Vector3 right = Vector3.Cross(up, forward);
                float side = Vector3.Dot(rest - l.girdle.position, right) >= 0f ? 1f : -1f;
                Vector3 power = (forward * st.direction.x + right * st.direction.y).normalized;
                return foreStroke.Offset(phase, st.powerFraction, power, right * side, up, l.size);
            }

            KickAxes(l, out var joint, out var back, out var outward, out var belly);
            Vector3 offset = Vector3.zero;
            if (l.size >= 1e-5f)
            {
                float s = hindKick.LoopAt(phase, st.powerFraction);
                // a braking kick pushes forward: the loop runs backward
                if (st.direction.x > 0f) s = Mathf.Repeat(0.5f - s, 1f);
                Vector3 p = hindKick.Point(s, Mathf.Max(l.size, hindKick.lightAmplitude));
                float yaw = l.yaw * Mathf.Deg2Rad, cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
                Vector3 swungBack = back * cos + outward * sin, swungOut = outward * cos - back * sin;
                Vector3 ankle = joint + l.length * (swungBack * p.x + swungOut * p.y + belly * p.z);
                offset = (ankle - rest) * hindKick.Engagement(l.size);
            }
            if (Fluke > 1e-3f) offset = Vector3.Lerp(offset, FlukeAnkle(l, joint, back, outward, belly) - rest, Fluke);
            // kicking, the feet are held off the midline; as a fluke they come in beside the tail
            float reach = Mathf.Abs(Vector3.Dot(rest - l.girdle.position, outward)) * hindKick.midlineReach;
            float inward = -Vector3.Dot(offset, outward);
            if (inward > 0f && reach > 1e-4f) offset += outward * ((1f - Fluke) * (inward - reach * (1f - Mathf.Exp(-inward / reach))));
            return offset;
        }

        // Sprinting prone: the leg held out behind along the hips, swinging up and down behind the hips' own pitch
        private Vector3 FlukeAnkle(in Limb l, Vector3 joint, Vector3 back, Vector3 outward, Vector3 belly)
        {
            var f = fluke;
            float a = (f.angle - f.sweep * FlukeDrive * Mathf.Cos(2f * Mathf.PI * (FlukePhase - f.lag))) * Mathf.Deg2Rad;
            return joint + l.length * (f.reach * (back * Mathf.Cos(a) + belly * Mathf.Sin(a)) + outward * f.outward);
        }

        // The fluke's web normal: flat to the hips, pitched into the water it meets. Sweeping toward the belly its
        // trailing edge rises, so the dorsal face tips forward. Signed to the face nearer the web's rest.
        private Vector3 FlukeNormal(in Limb l, Vector3 rest)
        {
            KickAxes(l, out _, out var back, out _, out var belly);
            float pitch = fluke.attack * FlukeDrive * Mathf.Sin(2f * Mathf.PI * (FlukePhase - fluke.lag)) * Mathf.Deg2Rad;
            Vector3 n = -belly * Mathf.Cos(pitch) - back * Mathf.Sin(pitch);
            return Vector3.Dot(rest, n) >= 0f ? n : -n;
        }

        // How far a hind foot's kick loop swings for the turn (HindTurn), from the side of the turn the leg is on now
        // (on the back the left leg is on the right), and whether the otter is swimming through the turn or turning on the spot.
        private float KickYaw(in Limb l, in LimbStroke st, float travel)
        {
            var t = hindTurn;
            float amount = Mathf.Abs(turn);
            if (amount < 1e-3f) return 0f;
            float side = Vector3.Dot(l.girdle.TransformPoint(l.jointLocal) - l.girdle.position, brain.State.right) >= 0f ? 1f : -1f;
            float yaw = side != Mathf.Sign(turn)
                ? t.outerYaw * amount
                : Mathf.Lerp(-t.innerYaw * amount, 90f, amount * (1f - travel) * t.innerSteer);
            return st.direction.x > 0f ? Mathf.Clamp(-yaw, -t.maxBrakeYaw, t.maxBrakeYaw) : yaw;
        }

        /// <summary>A hind kick's frame on the hip (world): the hip joint, back along the body, out toward the leg's side, and toward the belly.</summary>
        private static void KickAxes(in Limb l, out Vector3 joint, out Vector3 back, out Vector3 outward, out Vector3 belly)
        {
            Vector3 forward = l.girdle.TransformDirection(l.forwardLocal).normalized;
            Vector3 dorsal = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.upLocal), forward).normalized;
            Vector3 right = Vector3.Cross(dorsal, forward);
            joint = l.girdle.TransformPoint(l.jointLocal);
            back = -forward;
            belly = -dorsal;
            outward = Vector3.Dot(joint - l.girdle.position, right) >= 0f ? right : -right;
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
