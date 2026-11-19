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
    /// Hind foot kick, drawn in and kicked out. In the hip's own up-and-down plane, about hip width apart, the ankle
    /// runs a smooth closed loop between a tucked pose (knee drawn forward) and a stretched one (leg out behind), so the
    /// whole leg swings through the kick: the push kicks the foot out back, its web square to the back (FlipperPose),
    /// and the draw brings it forward again along the belly side of the push. On the belly the loop runs toward the
    /// belly, down into the water and clear of the tail, which runs back from the hips at their own level. On the back
    /// the belly side is the air, so there the loop runs on the other side: tucked, the foot sits under the hip with
    /// the knee drawn forward, and it kicks straight back and a little down past the sides of the tail. Lighter kicks
    /// run a smaller loop about the same middle. The loop rides on the hip bone, so it pitches and rolls with the spine.
    /// In time the push is quicker than the draw, and the foot eases through the turnovers at either end of the loop
    /// without stopping.
    /// </summary>
    [Serializable]
    public class HindKick
    {
        [Header("Shape: ankle from the hip joint, in leg lengths; angles from straight back toward the belly")]
        [Tooltip("Tucked pose at the start of the push: hip joint to ankle over the leg's length, and its angle (deg); past 90 the foot is drawn up ahead of the hip.")]
        public float tuckedReach = 0.42f;
        public float tuckedAngle = 125f;
        [Tooltip("Stretched pose at the end of the push. Low enough to drive the foot well back, high enough toward the belly that the push rises toward it and the foot clears the tail.")]
        public float stretchedReach = 0.98f;
        public float stretchedAngle = 45f;
        [Tooltip("Out from the hip joint (leg lengths) when tucked and when stretched: about hip width, the legs neither splayed nor crossing.")]
        public float tuckedOut = 0.15f;
        public float stretchedOut = 0.2f;
        [Tooltip("How far the push bows away from the belly and the draw comes back toward it, off the straight line from tucked to stretched (leg lengths), so the loop is not flat.")]
        public float pushBow = 0.05f;
        [Range(0f, 0.6f)] public float drawFold = 0.35f;
        [Tooltip("Where the knee points, from the hip joint: forward, toward the belly and out (leg lengths; the IK's pole). Drawn forward along the flank, not opened out to the side like a frog's; well off the tucked foot's line, so the knee never flips.")]
        public float kneeForward = 1f;
        public float kneeBelly = 0.3f;
        public float kneeOut = 0.15f;
        [Tooltip("Loop size at the lightest drive that kicks, as a fraction of the full loop.")]
        [Range(0.1f, 1f)] public float lightAmplitude = 0.5f;
        [Header("Timing")]
        [Tooltip("How fast the foot rounds the turnovers at the ends of the loop, as a share of its average pace over the cycle (0 = it stops there, 1 = it barely slows).")]
        [Range(0f, 1f)] public float turnover = 0.5f;
        [Header("Limits")]
        [Tooltip("How far toward the midline a foot may swing, as a fraction of its rest distance from it; held off softly so the feet never cross.")]
        [Range(0f, 1f)] public float midlineReach = 0.6f;

        /// <summary>Loop size at a drive, as a fraction of the full loop.</summary>
        public float Amplitude(float drive) => Mathf.Lerp(lightAmplitude, 1f, drive);

        /// <summary>Where on the loop the foot is at a phase: 0 = tucked, 0.5 = stretched, the push between, the draw after.
        /// Each half eases in and out at the same pace, so the foot rounds both ends without a jolt.</summary>
        public float LoopAt(float phase, float powerFraction)
        {
            float pf = Mathf.Clamp(powerFraction, 0.05f, 0.95f);
            if (phase < pf) return 0.5f * Ease(phase / pf, 2f * turnover * pf);
            return 0.5f + 0.5f * Ease((phase - pf) / (1f - pf), 2f * turnover * (1f - pf));
        }

        /// <summary>Ankle at loop position s on a loop of the given size, in leg lengths from the hip joint: x back along
        /// the body, y out toward the leg's side, z toward the belly.</summary>
        public Vector3 Point(float s, float amplitude)
        {
            Poses(amplitude, out var tuck, out var stretch);
            Vector3 mid = 0.5f * (tuck + stretch), half = 0.5f * (tuck - stretch);
            // off the straight line between the poses: the push bows away from the belly, the draw comes back on its
            // belly side, so the loop is not flat; both blend in and out smoothly around it
            Vector3 along = stretch - tuck;
            Vector3 belly = new Vector3(-along.z, 0f, along.x);
            belly = belly.sqrMagnitude > 1e-10f ? belly.normalized * amplitude : Vector3.zero;
            Vector3 bow = -belly * pushBow, fold = belly * drawFold;
            float a = 2f * Mathf.PI * s, cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            return mid + half * cos + 0.5f * (bow - fold) * sin + 0.5f * (bow + fold) * (sin * sin);
        }

        /// <summary>Middle of a loop of the given size, in the same frame as Point.</summary>
        public Vector3 Center(float amplitude)
        {
            Poses(amplitude, out var tuck, out var stretch);
            return 0.5f * (tuck + stretch);
        }

        // the two poses for a loop size, shrunk toward each other about their middle
        private void Poses(float amplitude, out Vector3 tuck, out Vector3 stretch)
        {
            float midAngle = 0.5f * (tuckedAngle + stretchedAngle), midReach = 0.5f * (tuckedReach + stretchedReach);
            float midOut = 0.5f * (tuckedOut + stretchedOut);
            tuck = Pose(midAngle + amplitude * (tuckedAngle - midAngle), midReach + amplitude * (tuckedReach - midReach), midOut + amplitude * (tuckedOut - midOut));
            stretch = Pose(midAngle + amplitude * (stretchedAngle - midAngle), midReach + amplitude * (stretchedReach - midReach), midOut + amplitude * (stretchedOut - midOut));
        }

        // back along the body and toward the belly at an angle (deg) from straight back, and out to the side
        private static Vector3 Pose(float angle, float reach, float outward) =>
            new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad) * reach, outward, Mathf.Sin(angle * Mathf.Deg2Rad) * reach);

        // 0..1 over u, leaving 0 and reaching 1 at a slope of m (monotonic up to m = 3)
        private static float Ease(float u, float m) => u * u * (3f - 2f * u) + m * u * (1f - u) * (1f - 2f * u);
    }

    /// <summary>
    /// Turning, the hind feet kick out to either side: each kick loop swings outward about its own middle, round the
    /// hip's dorso-ventral axis. The foot on the inside of the turn does the turning: it kicks back and out toward the
    /// middle of the turn, pushing the hindquarters the other way, so the head comes round. The outside foot kicks
    /// nearly straight back and carries the body on along its path. Turning on the spot, the inside foot's kick swings
    /// on round toward the middle of the turn, sculling the hindquarters around.
    /// </summary>
    [Serializable]
    public class HindTurn
    {
        [Tooltip("Yaw rate target (deg/s) that counts as a full turn.")]
        public float fullTurnRate = 90f;
        [Tooltip("Speed target (body lengths/s) from which the otter swims through its turns rather than turning on the spot.")]
        public float travelSpeedBL = 0.15f;
        [Tooltip("How far out the outside foot's kick swings, and the inside one's, at a full turn (deg).")]
        public float outerYaw = 8f;
        public float innerYaw = 45f;
        [Tooltip("Turning on the spot, how far round toward the middle of the turn (90 deg out) the inside foot's kick swings at a full turn.")]
        [Range(0f, 1f)] public float innerSteer = 0.75f;
        [Tooltip("Time constant of the kicks following the turn (s).")]
        public float response = 0.15f;
    }

    /// <summary>
    /// Sprinting prone the hind feet stop kicking: the legs stretch out behind along the hips, feet under the sides of
    /// the tail and webs flat, and heave up and down with the spine as one fluke, the feet running behind the hips like
    /// the end of a whip and the webs pitched into the water they meet; the tail keeps out of their way (TailRudder). The
    /// wave keeps the hind rhythm's beat; SpineKickWave puts it on the spine.
    /// </summary>
    [Serializable]
    public class FlukeStroke
    {
        public bool enabled = true;
        [Tooltip("Time constant of changing between kicking and the fluke (s).")]
        public float blendTime = 0.3f;
        [Header("Legs held out behind, from the hip joint")]
        [Tooltip("Ankle distance over the leg's length, and its angle toward the belly from straight back (deg): the legs run back under the sides of the tail, whose root sits just behind and above the hip joints and is wider than they are apart, so held straight back they would run through it.")]
        public float reach = 0.96f;
        public float angle = 25f;
        [Tooltip("How far out the feet sit (leg lengths, within the reach; negative = in toward the tail). The webs sweep up past the tail's root, so they must clear its sides.")]
        public float outward = 0.5f;
        [Header("Wave")]
        [Tooltip("How far the legs swing up and down about the hips on top of the hips' own pitch (deg), and how far behind the spine wave's beat they run (cycles).")]
        public float sweep = 44f;
        [Range(0f, 1f)] public float lag = 0.75f;
        [Tooltip("Web pitch into the water it meets: its trailing edge rises as it sweeps down and drops as it sweeps up, by this share of the angle its path through the water makes with the heading (a short flat plate drives hardest at about half), at most attack (deg).")]
        public float feather = 0.5f;
        public float attack = 40f;
        [Tooltip("Time constant of the web turning to its pitch (s), and the fastest it turns (deg/s). With little way on the pitch flips right over at each end of the beat; flipping slower than the beat turns, the web spends the stroke pitched the wrong way and pushes the body back.")]
        public float pitchTime = 0.02f;
        public float maxPitchSpeed = 2500f;
        [Tooltip("Most the fan twists in its own plane to trail its toes straight back (deg).")]
        public float maxTwistDeg = 70f;
        [Tooltip("Web width as a multiple of its rest width.")]
        public float width = 1.3f;
        [Tooltip("Spine pitch asked for at full drive (deg; the hips 1.5 times this, the chest 0.4), and how far the wave runs behind from chest to hips (rad per joint). The harmonizer's chain stiffness and damping take off about three quarters.")]
        public float spineAmplitude = 55f;
        public float spineWaveOffset = 0.8f;
    }

    /// <summary>
    /// Turns the hind flippers at the ankle and toes, on top of the IK. Kicking, the web hinges at the ankle, about the
    /// axis across the foot: from trailing straight back along the heading to square to it, toes toward the belly and
    /// turned out to the side, more so on the back, where the belly side is the surface. Through the push the ankle
    /// sets the sole square to the push and the web spreads; over the last of it the ankle points the foot, whipping the
    /// web back through the water. Through the draw the ankle is relaxed: the web folds back on its hinge to trail
    /// straight back along the heading, out of the water streaming past, and closes. Throughout it gives way to the
    /// water pressing on its top, so it never hooks forward against the flow.
    /// In the fluke the web lies level, toes trailing back, and pitches into the water it meets.
    /// </summary>
    [Serializable]
    public class FlipperPose
    {
        public bool enabled = true;
        [Header("Hinge at the ankle: from trailing straight back to square, toes toward the belly")]
        [Tooltip("How far the flexed toes turn out from the belly to the side, on the belly and on the back (deg). On the back the belly side is the surface: turned out, the standing web stays under it.")]
        public float turnOut = 15f;
        public float backTurnOut = 45f;
        [Header("Push: set square, then whipped back")]
        [Tooltip("How fast the ankle flexes the web on its own as the push begins (deg/s), while its sole still faces across the heading; the water pressing on the sole flexes it square.")]
        public float catchSpeed = 800f;
        [Tooltip("Last share of the push over which the ankle points the foot, whipping the web back.")]
        [Range(0f, 0.6f)] public float whip = 0.3f;
        [Header("Swing on the hinge")]
        [Tooltip("Time constant of the web swinging on its hinge (s), and the fastest it swings (deg/s).")]
        public float hingeTime = 0.02f;
        public float maxHingeSpeed = 1500f;
        [Header("Joints")]
        [Tooltip("Most the web turns from its rest on the shin (deg).")]
        public float maxTurnDeg = 135f;
        [Tooltip("Share of the turn taken at the ankle (the IK chain's end bone); the toes take the rest. Only when the IK leaves the end bone's rotation alone.")]
        [Range(0f, 1f)] public float ankleShare = 0.5f;
        [Tooltip("Most the fan twists in its own plane (deg) to bring its toes round to where the hinge points them.")]
        public float maxTwistDeg = 180f;
        [Tooltip("Time constant of the web following its hinge (s), and the fastest it turns (deg/s).")]
        public float turnTime = 0.02f;
        public float maxTurnSpeed = 2500f;
        [Tooltip("Time constant of the web taking up its kicking pose as a kick starts and lying back as it stops (s).")]
        public float standTime = 0.25f;
        [Tooltip("Web width while pushing and while drawing forward, as multiples of its rest width: toes spread wide and drawn together.")]
        public float spread = 1.25f;
        public float fold = 0.75f;
    }

    /// <summary>
    /// Moves the limb IK targets along stroke paths from OtterLocomotionBrain's rhythm: phase and strength per limb,
    /// so the limbs show what the allocation asks of them. Hind feet draw in and kick out on a loop about the hip joint
    /// (HindKick), splay out to either side in a turn (HindTurn), and sprinting prone hold out behind as a fluke
    /// (FlukeStroke); forepaws sweep along the allocated
    /// force direction, and rest folded on the chest when they have nothing to do, as on the back. Paths hang off the
    /// chest / hip bone, so they follow the spine's bend and roll. After the IK solve the hind flippers are turned at
    /// the ankle and toes (FlipperPose) to paddle, square through the push and trailing in the flow through the draw.
    /// Runs after the spine and before the IK solve. While enabled it switches off the older limb drivers that write
    /// the same targets (HindPaddleDriverRB, IKStrokeController), and switches them back on when disabled.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class LimbStrokeAnimator : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("The kick on the belly.")]
        public HindKick hindKick = new HindKick();
        [Tooltip("The kick on the back, under the water: tucked under and ahead of the hip and kicked straight back, every kick a full one.")]
        public HindKick backKick = new HindKick
        {
            tuckedReach = 0.35f, tuckedAngle = -120f, stretchedReach = 0.92f, stretchedAngle = -25f,
            tuckedOut = 0.3f, stretchedOut = 0.35f, kneeBelly = 0.6f, kneeOut = 0.2f, lightAmplitude = 1f,
        };
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
            /// <summary>Hind leg: the IK's pole, which this places, and where the scene put it.</summary>
            public Transform pole;
            public Vector3 poleRestLocal;

            // hind flipper: the chain's end bone (the foot, hinged at the ankle) and the toe fan below it
            public Transform end, web;
            public Quaternion endRest, webRest;
            /// <summary>Web normal in the flipper's space (the face that is dorsal at rest), and where it points now, in girdle space.</summary>
            public Vector3 normalLocal, aimLocal;
            /// <summary>The ankle's turn from its rest on the shin (deg).</summary>
            public float turned;
            /// <summary>Toe direction in the flipper's space, and the fan's twist in its plane (deg).</summary>
            public Vector3 toesLocal;
            public float twist;
            /// <summary>Middle of the web in the flipper's space.</summary>
            public Vector3 centerLocal;
            public Vector3 webScale;
            public int widthAxis;
            /// <summary>How far the ankle is set for the push, and the web spread: 1 through the power stroke, falling to 0
            /// as the whip points the foot at its end, 0 through the relaxed draw.</summary>
            public float push;
            public float width;
            /// <summary>The web's swing on its ankle hinge (deg, FlipperPose), and how far it has taken up its kicking pose from rest (0..1).</summary>
            public float hinge, stand;
            /// <summary>The ankle in the world last frame, for its path through the water.</summary>
            public Vector3 lastAnkle;
            public bool hasLastAnkle;
            /// <summary>In the fluke, which of the web's faces meets the water (±1, kept until the other is clearly nearer), 0 out of it.</summary>
            public float flukeFace;
            /// <summary>The web's middle in the world last frame where the leg carried it, before its turn, and the
            /// web's pitch into that path (deg).</summary>
            public Vector3 lastCarried;
            public bool hasLastCarried;
            public float flukePitch;
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

        /// <summary>A hind flipper's ankle turn from rest (deg), how far it is set for the push (0..1), its web width, and
        /// its swing on the ankle hinge (deg, 0 = trailing straight back, 90 = square); zeros for a forepaw.</summary>
        public Vector4 FlipperState(LimbId limb)
        {
            var l = limbs[(int)limb];
            return l.web != null ? new Vector4(l.turned, l.push, l.width, l.hinge) : Vector4.zero;
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
            foreach (var l in limbs)
                if (l.valid && l.pole != null) l.pole.localPosition = l.poleRestLocal;
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
                    pole = OtterRig.IsFore((LimbId)i) ? null : ik.pole,
                    poleRestLocal = ik.pole != null ? ik.pole.localPosition : Vector3.zero,
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
            l.twist = 0f;
            l.centerLocal = center;
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
            float goal = fluke.enabled && brain.Intent.gait == Gait.Dolphin ? 1f - brain.Posture.SupineFraction : 0f;
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
            float travel = Mathf.Clamp01(demand.speedTarget / Mathf.Max(1e-3f, hindTurn.travelSpeedBL * brain.Parameters.bodyLength));
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid) continue;
                var st = brain.Rhythm.limbs[i];
                l.size = Mathf.Lerp(l.size, TargetSize(i, l, st), grow);
                if (!OtterRig.IsFore((LimbId)i)) l.yaw = KickYaw(l, travel);
                float phase = st.active ? Mathf.Repeat(st.phase + st.rate * ahead, 1f) : 0f;
                Vector3 goal = l.restLocal + l.girdle.InverseTransformVector(StrokeOffset(i, l, st, phase));
                l.currentLocal = Vector3.Lerp(l.currentLocal, goal, blend);
                l.target.position = l.girdle.TransformPoint(l.currentLocal);
                if (l.pole != null)
                {
                    KickAxes(l, out var joint, out var back, out var outward, out var belly);
                    float supine = brain.Posture.SupineFraction;
                    l.pole.position = joint + l.length * (-back * Mathf.Lerp(hindKick.kneeForward, backKick.kneeForward, supine)
                        + belly * Mathf.Lerp(hindKick.kneeBelly, backKick.kneeBelly, supine) + outward * Mathf.Lerp(hindKick.kneeOut, backKick.kneeOut, supine));
                }
                if (l.end != null)
                {
                    float whipFrom = st.powerFraction * (1f - flipper.whip);
                    float set = phase >= st.powerFraction ? 0f
                        : phase <= whipFrom ? 1f
                        : 1f - Mathf.SmoothStep(0f, 1f, (phase - whipFrom) / Mathf.Max(1e-4f, st.powerFraction - whipFrom));
                    l.push = st.active && l.size > 1e-4f ? set : 0f;
                }
            }
        }

        private void PoseFlippers()
        {
            if (!isActiveAndEnabled || brain == null || !brain.HasLimbGeometry) return;
            var f = flipper;
            float kickTurn = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.turnTime));
            float flukeTurn = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, fluke.pitchTime));
            float standing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.standTime));
            float swing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.hingeTime));
            float turnOut = Mathf.Lerp(f.turnOut, f.backTurnOut, brain.Posture.SupineFraction) * Mathf.Deg2Rad;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.web == null) continue;
                float turn = Mathf.Lerp(kickTurn, flukeTurn, Fluke);
                // the IK carries the foot rigidly on the shin unless it aligns the end bone itself
                bool ankle = l.ik != null && !l.ik.alignEndRotation;
                if (ankle) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                // how much of a kick the foot is making: none at rest, full from the lightest kick up
                float kicking = f.enabled ? KickEngagement(l.size) : 0f;
                l.stand = Mathf.Lerp(l.stand, kicking, standing);
                float posed = Mathf.Max(l.stand, Fluke);
                Vector3 rest = l.web.TransformDirection(l.normalLocal);
                // the web's path through still water as the leg carries it, before its own turn: the turn swings the web
                // about the ankle the way it is already going, so pitching it by its posed path would run away
                Vector3 carried = l.web.TransformPoint(l.centerLocal);
                Vector3 water = l.hasLastCarried ? (carried - l.lastCarried) / Mathf.Max(1e-4f, Time.deltaTime) : Vector3.zero;
                l.lastCarried = carried;
                l.hasLastCarried = true;
                FlukePitch(ref l, water);
                Vector3 ankleAt = l.end.position;
                Vector3 flow = l.hasLastAnkle ? (ankleAt - l.lastAnkle) / Mathf.Max(1e-4f, Time.deltaTime) : Vector3.zero;
                l.lastAnkle = ankleAt;
                l.hasLastAnkle = true;
                Hinge(ref l, flow, turnOut, swing, out var sole, out var toesHinged);
                Vector3 aim = l.girdle.TransformDirection(l.aimLocal);
                Vector3 goal = Vector3.Slerp(rest, sole, l.stand);
                if (Fluke > 1e-3f) goal = Vector3.Slerp(goal, FlukeNormal(ref l, rest, aim), Fluke);
                else l.flukeFace = 0f;
                // the aim eases toward the goal in the hip's space, and the ankle is solved fresh from the shin each
                // frame, so the web holds its line while the shin swings under it
                float turnSpeed = Mathf.Lerp(f.maxTurnSpeed, fluke.maxPitchSpeed, Fluke);
                Vector3 next = Vector3.RotateTowards(aim, Vector3.Slerp(aim, goal, turn), turnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
                next = Vector3.RotateTowards(rest, next, Mathf.Min(Vector3.Angle(rest, next), f.maxTurnDeg + 15f) * Mathf.Deg2Rad, 0f);
                l.aimLocal = l.girdle.InverseTransformDirection(next);
                l.turned = Vector3.Angle(rest, next);
                // the ankle takes its share of the turn and the toes the rest, so neither joint folds over alone
                Quaternion whole = Quaternion.FromToRotation(rest, next);
                if (ankle) l.end.rotation = Quaternion.Slerp(Quaternion.identity, whole, f.ankleShare) * l.end.rotation;
                l.web.rotation = Quaternion.FromToRotation(l.web.TransformDirection(l.normalLocal), next) * l.web.rotation;
                // the least turn can swing the toes anywhere in the new plane; twist them round to where the hinge
                // points them, or to trail straight back along the fluke
                Vector3 toesNow = l.web.TransformDirection(l.toesLocal);
                Vector3 toesHome = Vector3.Slerp(toesHinged, -l.girdle.TransformDirection(l.forwardLocal), Fluke);
                Vector3 toesWant = Vector3.ProjectOnPlane(toesHome, next);
                float maxTwist = Mathf.Lerp(f.maxTwistDeg, fluke.maxTwistDeg, Fluke);
                float twistGoal = toesWant.sqrMagnitude > 1e-4f
                    ? Mathf.Clamp(Vector3.SignedAngle(toesNow, toesWant, next), -maxTwist, maxTwist) * posed
                    : l.twist;
                float twistStep = Mathf.DeltaAngle(l.twist, twistGoal) * turn;
                l.twist = Mathf.Repeat(l.twist + Mathf.Clamp(twistStep, -f.maxTurnSpeed * Time.deltaTime, f.maxTurnSpeed * Time.deltaTime) + 180f, 360f) - 180f;
                l.web.rotation = Quaternion.AngleAxis(l.twist, next) * l.web.rotation;

                float kickWidth = Mathf.Lerp(1f, Mathf.Lerp(f.fold, f.spread, l.push), kicking);
                l.width = Mathf.Lerp(l.width, Mathf.Lerp(kickWidth, fluke.width, Fluke), turn);
                var scale = l.webScale;
                scale[l.widthAxis] *= l.width;
                l.web.localScale = scale;
            }
        }

        // The web's swing on its ankle hinge (FlipperPose), from trailing straight back along the heading (0) to square to
        // it (90 deg, toes toward the belly): set square for the push, then folded back through the whip; relaxed, it
        // trails, the way the water streams past the swimming body, so whatever the water does to it there is across the
        // heading. Either way it is a flap the water holds: its sole turns into the water only as fast as the ankle's own
        // motion presses it up (flexing faster, it would sweep its face forward through the water and brake the body), and
        // it gives way when the water presses on its top. As a push begins the ankle catches the water, flexing the web on
        // its own while its sole still faces across the heading, where the water it sweeps pushes the body up or down
        // rather than back; turned square, only the water flexes it further. Its toes turn out only as far as it is
        // flexed. Out: the sole's normal (the face dorsal at rest) and where the toes point.
        private void Hinge(ref Limb l, Vector3 flow, float turnOut, float swing, out Vector3 sole, out Vector3 toes)
        {
            var f = flipper;
            KickAxes(l, out _, out _, out var outward, out var belly);
            LevelAxes(l, out var back, out _);
            float outAngle = turnOut * Mathf.Clamp01(l.hinge / 90f);
            Vector3 flex = Vector3.ProjectOnPlane(belly * Mathf.Cos(outAngle) + outward * Mathf.Sin(outAngle), back).normalized;
            float most = f.maxHingeSpeed * Time.deltaTime;
            float step = Mathf.Clamp((90f * l.push - l.hinge) * swing, -most, most);
            float a = l.hinge * Mathf.Deg2Rad;
            Vector3 face = back * Mathf.Sin(a) - flex * Mathf.Cos(a);
            float lever = Mathf.Max(0.01f, Vector3.Distance(l.web.TransformPoint(l.centerLocal), l.end.position));
            float pressed = Vector3.Dot(flow, face) / lever * Mathf.Rad2Deg;
            float caught = f.catchSpeed * l.push * (1f - Mathf.Clamp01(Vector3.Dot(face, back)));
            step = Mathf.Min(step, (pressed + caught) * Time.deltaTime);
            l.hinge = Mathf.Clamp(l.hinge + step, 0f, 90f);
            a = l.hinge * Mathf.Deg2Rad;
            toes = back * Mathf.Cos(a) + flex * Mathf.Sin(a);
            sole = back * Mathf.Sin(a) - flex * Mathf.Cos(a);
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
                l.hinge = 0f;
                l.stand = 0f;
                l.hasLastAnkle = false;
                l.flukeFace = 0f;
                l.hasLastCarried = false;
                l.flukePitch = 0f;
            }
        }

        private float TargetSize(int i, in Limb l, in LimbStroke st)
        {
            // a limb with nothing left to do shrinks back to rest while its cycle runs out, e.g. forepaws folding as the otter rolls onto its back
            if (!st.active || st.parking) return 0f;
            return OtterRig.IsFore((LimbId)i)
                ? foreStroke.Length(l.length, st.drive)
                : Mathf.Lerp(hindKick.Amplitude(st.drive), backKick.Amplitude(st.drive), brain.Posture.SupineFraction);
        }

        // the lightest kick's loop size, on the belly or the back or rolling between
        private float LightKick => Mathf.Lerp(hindKick.lightAmplitude, backKick.lightAmplitude, brain.Posture.SupineFraction);

        /// <summary>How far a kick growing in from rest (size 0) or fading out to it has moved onto its loop.</summary>
        private float KickEngagement(float size) => Mathf.Clamp01(size / LightKick);

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
                float amplitude = Mathf.Max(l.size, LightKick), supine = brain.Posture.SupineFraction;
                Vector3 p = Vector3.Lerp(hindKick.Point(hindKick.LoopAt(phase, st.powerFraction), amplitude),
                                         backKick.Point(backKick.LoopAt(phase, st.powerFraction), amplitude), supine);
                Vector3 m = Vector3.Lerp(hindKick.Center(amplitude), backKick.Center(amplitude), supine);
                // the turn swings the loop out about its own middle, so the leg doesn't swing bodily across the belly
                float yaw = l.yaw * Mathf.Deg2Rad, cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
                Vector3 swungBack = back * cos + outward * sin, swungOut = outward * cos - back * sin;
                Vector3 ankle = joint + l.length * (back * m.x + outward * m.y + belly * p.z + swungBack * (p.x - m.x) + swungOut * (p.y - m.y));
                offset = (ankle - rest) * KickEngagement(l.size);
            }
            // the kick's loop runs up ahead of the hip, far from the fluke's line behind it, so it gives way early
            if (Fluke > 1e-3f) offset = Vector3.Lerp(offset, FlukeAnkle(l, joint, back, outward, belly) - rest, 1f - (1f - Fluke) * (1f - Fluke));
            // kicking, the feet are held off the midline; as a fluke FlukeStroke places them
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
            float along = Mathf.Sqrt(Mathf.Max(0f, f.reach * f.reach - f.outward * f.outward));
            return joint + l.length * (along * (back * Mathf.Cos(a) + belly * Mathf.Sin(a)) + outward * f.outward);
        }

        // The fluke pitches the web into the water it meets, from the way it actually moves through it: the hips' own
        // heave and pitch carry the foot as much as the leg's sweep does, and the web whips behind the ankle, so a beat
        // timed from the rhythm alone would pitch it out of step with its path and push as much back as on
        private void FlukePitch(ref Limb l, Vector3 water)
        {
            LevelAxes(l, out var back, out var belly);
            float ahead = -Vector3.Dot(water, back), down = Vector3.Dot(water, belly);
            float path = Mathf.Atan2(down, Mathf.Max(ahead, 0.02f)) * Mathf.Rad2Deg;
            l.flukePitch = Mathf.Clamp(fluke.feather * path, -fluke.attack, fluke.attack);
        }

        // The fluke's web normal: level, pitched into its path. Sweeping toward the belly its trailing edge rises, so the
        // dorsal face tips forward. Signed to the face nearer the web's rest.
        // Both faces of the web push alike, so it keeps the face it shows: turning over would sweep it face-on through
        // the water ahead. It takes the other only once that is clearly nearer, as when a paddling web stood square to
        // the back lies down into the fluke, or once the shin has carried its face out of the ankle's reach.
        private Vector3 FlukeNormal(ref Limb l, Vector3 rest, Vector3 aim)
        {
            LevelAxes(l, out var back, out var belly);
            float pitch = l.flukePitch * Mathf.Deg2Rad;
            Vector3 n = -belly * Mathf.Cos(pitch) - back * Mathf.Sin(pitch);
            if (l.flukeFace == 0f || Vector3.Dot(aim, n * l.flukeFace) < -0.3f) l.flukeFace = Vector3.Dot(aim, n) >= 0f ? 1f : -1f;
            n *= l.flukeFace;
            if (Vector3.Angle(rest, n) > flipper.maxTurnDeg && Vector3.Angle(rest, -n) <= flipper.maxTurnDeg)
            {
                l.flukeFace = -l.flukeFace;
                n = -n;
            }
            return n;
        }

        // How far out a hind foot's kick loop swings for the turn (HindTurn), from the side of the turn the leg is on now
        // (on the back the left leg is on the right), and whether the otter is swimming through the turn or turning on the spot.
        private float KickYaw(in Limb l, float travel)
        {
            var t = hindTurn;
            float amount = Mathf.Abs(turn);
            if (amount < 1e-3f) return 0f;
            float side = Vector3.Dot(l.girdle.TransformPoint(l.jointLocal) - l.girdle.position, brain.State.right) >= 0f ? 1f : -1f;
            return side != Mathf.Sign(turn)
                ? t.outerYaw * amount
                : Mathf.Lerp(t.innerYaw * amount, 90f, amount * (1f - travel) * t.innerSteer);
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

        // Back along the body's heading through the water and the hips' belly side square to it: the hips' own pitch
        // left out, since the push that drives the body on is along the heading; their roll kept, for the roll-over.
        // The heading is taken in the hips' up-and-down plane: rolling over, the hips come round after the chest, and
        // their pitch in the world then lies across them, which would turn the webs aside into the tail.
        private void LevelAxes(in Limb l, out Vector3 back, out Vector3 belly)
        {
            KickAxes(l, out _, out var hipBack, out var outward, out var hipBelly);
            Vector3 heading = Vector3.ProjectOnPlane(brain.State.forward, outward);
            back = heading.sqrMagnitude > 0.25f * brain.State.forward.sqrMagnitude && heading.sqrMagnitude > 1e-6f ? -heading.normalized : hipBack;
            Vector3 level = Vector3.ProjectOnPlane(hipBelly, back);
            belly = level.sqrMagnitude > 1e-6f ? level.normalized : hipBelly;
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
