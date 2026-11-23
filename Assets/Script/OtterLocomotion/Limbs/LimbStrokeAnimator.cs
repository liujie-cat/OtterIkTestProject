using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Forepaw paddle, round the shoulder. For the catch the shoulder blade reaches the shoulder forward and up, and the
    /// paw goes in out ahead under the chin; the arm then sweeps a wide arc back and out round the shoulder, stretched,
    /// the paw square to its sweep, while the shoulder blade draws the shoulder back. For the recovery the paw turns
    /// edge-on and slices forward again folded in close to the body. The arc lies in a plane through the shoulder
    /// tilted down from the chest's level toward the belly, so the paw stays in the water, and rides on the chest bone,
    /// so it turns, banks and pitches with the chest. Sculling out, the oar on the outside of a cruise turn, it sweeps
    /// from ahead round to the side; a push back turns it further round, a brake further forward.
    /// </summary>
    [Serializable]
    public class ForePaddle
    {
        [Header("Arc round the shoulder: angles from straight ahead round toward the side (deg)")]
        [Tooltip("Where the sweep catches the water and where it finishes, sculling out at full drive.")]
        public float catchAngle = 10f;
        public float finishAngle = 155f;
        [Tooltip("Tilt of the arc's plane from the chest's level down toward the belly (deg).")]
        public float dip = 25f;
        [Tooltip("How far the push turns the arc round from sculling straight out (deg): back for a thrust, forward for a brake.")]
        public float maxTurnBack = 35f;
        public float maxTurnForward = 45f;
        [Tooltip("Arc span at the lightest drive that paddles, as a share of the full one.")]
        [Range(0.1f, 1f)] public float lightAmplitude = 0.5f;
        [Header("Reach: shoulder to wrist over the arm's length")]
        [Tooltip("Stretched through the sweep, folded in close through the recovery.")]
        public float sweepReach = 0.95f;
        public float recoveryReach = 0.5f;
        [Tooltip("How far the sweep bows down toward the belly at its middle, and the recovery comes up in toward the chest (arm lengths).")]
        public float sweepDepth = 0.1f;
        public float recoveryLift = 0.15f;
        [Header("Shoulder blade")]
        [Tooltip("How far it reaches the shoulder forward for the catch and draws it back at the finish, and lifts it at the catch (m).")]
        public float protract = 0.03f;
        public float retract = 0.02f;
        public float lift = 0.012f;
        [Header("Paw")]
        [Tooltip("Share of the recovery at either end over which the paw turns between square and edge-on.")]
        [Range(0.05f, 0.5f)] public float featherTurn = 0.2f;
        [Tooltip("The most the wrist turns the paw from where the forearm carries it (deg).")]
        public float wristRange = 100f;
        [Tooltip("Where the elbow points while paddling, from the shoulder (arm lengths): back along the body, up toward the back, out to the side.")]
        public float elbowBack = 1f;
        public float elbowUp = 0.5f;
        public float elbowOut = 0.2f;

        /// <summary>Arc span at a drive, as a share of the full one.</summary>
        public float Amplitude(float drive) => Mathf.Lerp(lightAmplitude, 1f, drive);

        /// <summary>How far round the arc the paw is at a phase: 0 at the catch, 1 at the finish; out through the sweep, back through the recovery.</summary>
        public static float Along(float phase, float powerFraction)
        {
            float pf = Mathf.Clamp(powerFraction, 0.05f, 0.95f);
            return phase < pf ? Smooth(phase / pf) : 1f - Smooth((phase - pf) / (1f - pf));
        }

        /// <summary>Wrist at a phase on an arc of the given span turned round by <paramref name="turn"/> (deg, + = back),
        /// in arm lengths from the shoulder: x forward, y out to the paw's side, z toward the belly.</summary>
        public Vector3 Point(float phase, float powerFraction, float amplitude, float turn)
        {
            float pf = Mathf.Clamp(powerFraction, 0.05f, 0.95f);
            bool sweep = phase < pf;
            float bulge = Mathf.Sin(Mathf.PI * (sweep ? phase / pf : (phase - pf) / (1f - pf)));
            float reach = sweep ? sweepReach : Mathf.Lerp(sweepReach, recoveryReach, bulge);
            float down = sweep ? sweepDepth * bulge : -recoveryLift * bulge;
            float mid = 0.5f * (catchAngle + finishAngle) + turn, half = 0.5f * (finishAngle - catchAngle) * amplitude;
            float a = (mid - half + 2f * half * Along(phase, powerFraction)) * Mathf.Deg2Rad, d = dip * Mathf.Deg2Rad;
            float side = Mathf.Sin(a) * reach;
            return new Vector3(Mathf.Cos(a) * reach, side * Mathf.Cos(d), side * Mathf.Sin(d) + down);
        }

        /// <summary>Where the shoulder blade takes the shoulder at a point round the arc (m): forward, and up toward the back.</summary>
        public Vector2 Shoulder(float along) => new Vector2(Mathf.Lerp(protract, -retract, along), lift * (1f - along) * (1f - along));

        /// <summary>How square the paw is at a phase: 1 through the sweep, 0 edge-on through the recovery.</summary>
        public float Square(float phase, float powerFraction)
        {
            float pf = Mathf.Clamp(powerFraction, 0.05f, 0.95f);
            if (phase < pf) return 1f;
            float u = (phase - pf) / (1f - pf);
            return Mathf.SmoothStep(0f, 1f, Mathf.Max(1f - u / featherTurn, (u - 1f + featherTurn) / featherTurn));
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

        /// <summary>A point on the line from the tucked pose (0) to the stretched one (1) of a loop of the given size, in the same frame as Point.</summary>
        public Vector3 Along(float amplitude, float t)
        {
            Poses(amplitude, out var tuck, out var stretch);
            return Vector3.Lerp(tuck, stretch, t);
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
    /// Turning, the hind feet kick out to either side: the hip opens each leg out round its dorso-ventral axis as the
    /// leg stretches through the push, none of it tucked and all of it stretched, and closes it again through the draw.
    /// Turned at the hip joint, the foot keeps its reach all round the loop: the leg neither folds up into the hip nor
    /// runs out of reach. The foot on the inside of the turn does the turning: it kicks back and out toward the
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
    /// Sprinting prone the hind feet stop kicking: the legs stretch out behind along the hips, feet together under the
    /// tail and webs flat side by side, and heave up and down with the spine as one fluke, the feet running behind the
    /// hips like the end of a whip and the webs pitched into the water they meet; the tail keeps out of their way
    /// (TailRudder). The wave keeps the hind rhythm's beat, and the body swims along it (DolphinTrack).
    /// </summary>
    [Serializable]
    public class FlukeStroke
    {
        public bool enabled = true;
        [Tooltip("Time constant of changing between kicking and the fluke (s).")]
        public float blendTime = 0.3f;
        [Header("Legs held out behind, from the hip joint")]
        [Tooltip("Ankle distance over the leg's length, and its angle toward the belly from straight back (deg): the legs run back under the tail, whose root sits just behind and above the hip joints, so held straight back they would run through it.")]
        public float reach = 0.96f;
        public float angle = 40f;
        [Tooltip("Gap between the webs' inner edges (m): each ankle sits half its web's width out from the hips' midline, so the webs lie together as one fluke.")]
        public float apart = 0.005f;
        [Header("Wave")]
        [Tooltip("How far the legs swing up and down about the hips on top of the hips' own pitch (deg), and how far behind the beat they run (cycles).")]
        public float sweep = 44f;
        [Range(0f, 1f)] public float lag = 0.75f;
        [Tooltip("Web pitch into the water it meets: its trailing edge rises as it sweeps down and drops as it sweeps up, by this share of the angle its path through the water makes with the heading (a short flat plate drives hardest at about half), at most attack (deg).")]
        public float feather = 0.5f;
        public float attack = 40f;
        [Tooltip("Time constant of the web turning to its pitch (s). With little way on the pitch flips right over at each end of the beat; flipping slower than the beat turns, the web spends the stroke pitched the wrong way and pushes the body back.")]
        public float pitchTime = 0.02f;
        [Tooltip("Web width as a multiple of its rest width.")]
        public float width = 1.3f;
    }

    /// <summary>
    /// Standing up in the water (Periscope) the hind feet tread water, pedalling half a cycle apart so one or the other
    /// always presses down. Each ankle runs an upright ellipse below the hip joint and a little ahead of it: the push
    /// comes down its front side, down and a little back, the web spread with its sole square to the push; the
    /// recovery goes up its back side, the ankle relaxed, so the web trails down edge-on and slices up through the
    /// water (FlipperPose, hinged about the push). The ankle turns the web over at the top and the bottom, where the
    /// foot barely moves up or down. The more lift the stand asks of the treading (Periscope.Lift), the deeper and
    /// quicker the strokes. The ellipse is set in the world, upright whatever the spine's lean. Turning there, each
    /// foot's push tilts from down toward a sweep of its own: the outside foot's back and out along its side, in
    /// bigger strokes, the inside one's forward and in, in smaller ones, so the two push the hips round as a couple
    /// while still holding the body up.
    /// </summary>
    [Serializable]
    public class HindTread
    {
        public bool enabled = true;
        [Header("Ellipse from the hip joint, in leg lengths")]
        [Tooltip("Its middle: down from the hip joint, ahead of it toward where the belly faces, and out to the leg's side.")]
        public float down = 0.62f;
        public float ahead = 0.15f;
        public float outward = 0.2f;
        [Tooltip("Half its length along the push and half its width across it, at full size.")]
        public float depth = 0.36f;
        public float width = 0.08f;
        [Tooltip("How far the push leans back from straight down (deg).")]
        public float lean = 15f;
        [Tooltip("Where the knee points from the hip joint: ahead and out (leg lengths; the IK's pole).")]
        public float kneeAhead = 1f;
        public float kneeOut = 0.3f;
        [Tooltip("Share of the stroke over which the web turns over between square and edge-on, about the top and about the bottom.")]
        [Range(0.05f, 0.4f)] public float turnover = 0.2f;
        [Header("Size and pace")]
        [Tooltip("Lift asked of the treading (N) that takes the strokes to their full size and pace.")]
        public float fullLift = 3f;
        [Tooltip("Stroke size with no lift asked, as a share of the full one.")]
        [Range(0.1f, 1f)] public float lightAmplitude = 0.5f;
        [Tooltip("Strokes per second with no lift asked and at full lift.")]
        public float lightRate = 0.6f;
        public float fullRate = 0.9f;
        [Header("Turning, at a full turn (HindTurn.fullTurnRate)")]
        [Tooltip("How far each foot's push tilts from down toward its sweep (deg).")]
        public float sweep = 35f;
        [Tooltip("Which way the outside foot sweeps: back, turned out toward its side (deg); and the inside one: forward, turned in (deg).")]
        public float outerSwing = 30f;
        public float innerSwing = 30f;
        [Tooltip("How much bigger the outside foot's strokes get and how much smaller the inside one's, as shares of the stroke.")]
        public float outerGrow = 0.3f;
        public float innerShrink = 0.3f;

        /// <summary>How square the web is at a phase (0 = top, the push to 0.5, the recovery after): 1 through the push, 0 edge-on through the recovery.</summary>
        public float Square(float phase)
        {
            float u = Mathf.Repeat(phase + 0.5f * turnover, 1f);
            return u < turnover ? Mathf.SmoothStep(0f, 1f, u / turnover)
                : u < 0.5f ? 1f
                : u < 0.5f + turnover ? 1f - Mathf.SmoothStep(0f, 1f, (u - 0.5f) / turnover)
                : 0f;
        }
    }

    /// <summary>
    /// Turns the hind flippers at the ankle and toes, on top of the IK. Kicking, the web hinges at the ankle, about the
    /// axis across the foot: from trailing straight back along the heading to square to it, toes toward the belly and
    /// turned out to the side, more so on the back, where the belly side is the surface. Through the push the ankle
    /// sets the sole square to the push and the web spreads; over the last of it the ankle points the foot, whipping the
    /// web back through the water. Through the draw the ankle is relaxed: the web folds back on its hinge to trail
    /// straight back along the heading, out of the water streaming past, and closes. Throughout it gives way to the
    /// water pressing on its top, so it never hooks forward against the flow. The hinge is set in the kick's own frame,
    /// its loop swung out for a turn, so the web pushes square to the way the leg actually sweeps. Treading water
    /// (HindTread) the push is down: square, the sole presses down, toes ahead; relaxed, the toes trail down.
    /// In the fluke the web lies level, toes trailing back, and pitches into the water it meets.
    /// The web is carried on the shin by its joints: the ankle flexes it about the axis across the foot, and it turns out
    /// about the foot's length and splays in its own plane, each only so far. Its pose is held in the hip's space, so it
    /// keeps its line while the shin swings under it, as those three angles within their ranges: wherever the leg takes
    /// the foot, the web turns only as far as its joints let it and stops there, rather than swinging the long way round.
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
        [Header("Joints, from the web's rest in the plane the leg bends in")]
        [Tooltip("How far the ankle flexes the web about the axis across the foot, parallel to the knee (deg): + the toes toward the belly side, the sole toward where they pointed (90 = square to the shin), - pointing the foot. On the back the shin hangs down from the raised knee, so the web trailing back is already flexed about 90.")]
        public Vector2 flex = new Vector2(-80f, 155f);
        [Tooltip("How far the sole then turns about the foot's length, + out to the leg's side (deg). Kept well short of 90, where flex and splay would lose their meaning.")]
        public Vector2 evert = new Vector2(-45f, 70f);
        [Tooltip("How far the toes then splay in the web's own plane, + out to the leg's side (deg). On the back the leg bends in a plane skewed out from the knee, and the web turned out under the surface lies well round from the shin's line.")]
        public Vector2 splay = new Vector2(-145f, 55f);
        [Tooltip("How far short of each end of a joint's range its stop begins to take the web along as the leg swings under it (deg). The web eases onto the leg's swing instead of being caught at the end all at once.")]
        public float cushion = 20f;
        [Tooltip("Share of the flex taken at the ankle (the IK chain's end bone); the toe fan takes the rest of the turn. Only when the IK leaves the end bone's rotation alone.")]
        [Range(0f, 1f)] public float ankleShare = 0.5f;
        [Tooltip("Time constant of the web following its hinge (s). The water damps it far more than its weight carries it on, so it eases straight in, never swinging past.")]
        public float turnTime = 0.02f;
        [Tooltip("Time constant of the web taking up its kicking pose as a kick starts and lying back as it stops (s).")]
        public float standTime = 0.25f;
        [Tooltip("Web width while pushing and while drawing forward, as multiples of its rest width: toes spread wide and drawn together.")]
        public float spread = 1.25f;
        public float fold = 0.75f;
    }

    /// <summary>
    /// On the back the forepaws lie on the chest and belly, the arms folded in from the sides, rather than where they
    /// hang under the chest on the belly: turned up, that is under the chin, and the head curled over to look along the
    /// belly buries them. The chest and belly are measured from the skin when the limbs are captured. The arms are too
    /// short to take the wrists out from under the chin, so each paw lies flat, palm to the chest, its fingers reaching
    /// back along the belly; the forearm alone would carry it tilted, its fingers into the chest. In the dolphin wave
    /// (FlukeStroke) they tuck in there on the belly too, out of the water streaming past, and ride with the chest, and
    /// standing up in the water (Periscope) they are held there to the chest.
    /// </summary>
    [Serializable]
    public class ChestRest
    {
        public bool enabled = true;
        [Tooltip("How far toward their place on the chest and belly the forepaws tuck in the dolphin wave.")]
        [Range(0f, 1f)] public float sprintTuck = 1f;
        [Tooltip("How far out from the midline each wrist lies, as a share of the trunk's half width.")]
        [Range(0f, 1f)] public float apart = 0.4f;
        [Tooltip("How far the wrist reaches back over the chest from the shoulder, as a share of the arm's length: as far back from the head as the arm lies at ease.")]
        [Range(0.3f, 1f)] public float reach = 0.9f;
        [Tooltip("Wrist height above the skin (m): the paw's thickness from the wrist to its palm, and a little more.")]
        public float lift = 0.012f;
        [Tooltip("How far the fingers turn in toward the midline from pointing straight back along the belly (deg).")]
        [Range(0f, 90f)] public float inward = 25f;
    }

    /// <summary>
    /// A forepaw in the water trails the chest's turning: as the chest swings it round on its shoulder the water holds it
    /// back, and it catches up as the chest settles, so it lags the chest's turn, bank and pitch by the chest's turning
    /// rate times lagTime. A stroking paw drives itself; one out of the water, on the chest on the back, rides with it
    /// and eases back from however far it trailed.
    /// </summary>
    [Serializable]
    public class ForeFollow
    {
        public bool enabled = true;
        [Tooltip("Time constant of a trailing paw easing back to rest on the chest (s).")]
        public float lagTime = 0.08f;
        [Tooltip("The most a paw trails the chest (deg).")]
        public float maxLag = 40f;
        [Tooltip("Depth over which the wrist goes from out of the water to in it (m), about the paw's thickness.")]
        public float wetDepth = 0.01f;
    }

    /// <summary>
    /// Forepaws in a roll over, paced by how far the chest has rolled, as the spine actually rolls it. The paw on the
    /// side the body rolls down onto takes one big stroke across the roll, in the plane the shoulder turns in about the
    /// chest's length: reaching out for the catch toward the side facing down as the roll starts (the belly, rolling
    /// onto the back; the back, rolling onto the belly) and riding round with the chest, it sweeps round the shoulder
    /// to the side facing down as the roll ends, faster than the chest rolls, so it goes back through the water against
    /// the roll and pulls the body over; the shoulder blade reaches it out and draws it back. The spine sets off fast
    /// and slows as it settles, so the sweep is in the later part of the roll, which a shoulder can outrun; as the roll
    /// settles the paw comes round, folding in to its new rest. The paw on the side coming up presses in, its shoulder
    /// blade drawn back, and is at its new rest by the time the chest is half over.
    /// </summary>
    [Serializable]
    public class ForeRoll
    {
        public bool enabled = true;
        [Header("Arc round the shoulder: angles from straight out toward the side facing down (deg)")]
        [Tooltip("Where the arm catches the water, toward the side facing down as the roll starts, and where it finishes, toward the side facing down as it ends: the sweep between them must outrun the chest's roll through it.")]
        public float catchAngle = 60f;
        public float finishAngle = 60f;
        [Tooltip("Where the arm comes round to, toward the belly, as it folds in to its new rest.")]
        public float foldAngle = 70f;
        [Tooltip("How far ahead of the shoulder the wrist reaches at the catch and at the finish (arm lengths).")]
        public float catchForward = 0.35f;
        public float finishForward = -0.1f;
        [Tooltip("Shoulder to wrist over the arm's length through the sweep, and folded in at its new rest.")]
        public float reach = 0.95f;
        public float foldReach = 0.5f;
        [Header("Pace: shares of the roll (the angle rolled over 180 deg)")]
        [Tooltip("By when the arm has reached out for its catch, where its sweep starts and finishes, and by when the paw coming up has settled at its new rest.")]
        [Range(0.01f, 0.5f)] public float reachBy = 0.15f;
        [Range(0f, 0.8f)] public float sweepFrom = 0.4f;
        [Range(0.3f, 0.95f)] public float finishAt = 0.85f;
        [Range(0.05f, 1f)] public float settleBy = 0.5f;
        [Tooltip("Roll the chest keeps from the bank into a turn not quite taken out (deg); within it of either posture the roll is over.")]
        public float settled = 10f;
        [Tooltip("Time constant of the stroke taking hold and letting go (s): the chest rolls fastest as it sets off.")]
        public float holdTime = 0.04f;

        /// <summary>How much of the stroke the leading paw is taking at a share of the roll: reaching out, then folding in.</summary>
        public float Hold(float progress) =>
            Mathf.SmoothStep(0f, 1f, progress / reachBy) * (1f - Mathf.SmoothStep(0f, 1f, (progress - finishAt) / (1f - finishAt)));

        /// <summary>The leading wrist's direction from the shoulder at a share of the roll, in the shoulder's frame (x forward,
        /// y out, z toward the belly) and over the arm's length; how far round the sweep it is (0 at the catch, 1 at the
        /// finish) and how far it has folded in. <paramref name="toBack"/>: rolling onto the back, the belly faces down first.</summary>
        public Vector3 Point(float progress, bool toBack, out float along, out float fold)
        {
            along = Mathf.SmoothStep(0f, 1f, (progress - sweepFrom) / Mathf.Max(1e-3f, finishAt - sweepFrom));
            fold = Mathf.SmoothStep(0f, 1f, (progress - finishAt) / (1f - finishAt));
            float down = toBack ? 1f : -1f;
            float a = Mathf.Lerp(down * Mathf.Lerp(catchAngle, -finishAngle, along), foldAngle, fold) * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Lerp(catchForward, finishForward, along), Mathf.Cos(a), Mathf.Sin(a)).normalized;
            return dir * Mathf.Lerp(reach, foldReach, fold);
        }
    }

    /// <summary>
    /// Moves the limb IK targets along stroke paths from OtterLocomotionBrain's rhythm: phase and strength per limb,
    /// so the limbs show what the allocation asks of them. Hind feet draw in and kick out on a loop about the hip joint
    /// (HindKick), splay out to either side in a turn (HindTurn), sprinting prone hold out behind as a fluke
    /// (FlukeStroke), and standing up in the water tread it (HindTread); forepaws paddle round the shoulder, the
    /// shoulder blade reaching and drawing it (ForePaddle), and rest when they have nothing to do: under the chest on the belly, on
    /// the chest and belly on the back and tucked there in the dolphin wave (ChestRest), and standing up in the water
    /// (Periscope) they are held there and do not paddle; in the water they trail the chest's turning (ForeFollow). Rolling
    /// over, the paw on the side going down sweeps back through the water against the roll and the other settles at its
    /// new rest, both as far as the chest has actually rolled (ForeRoll). Paths hang
    /// off the chest / hip bone, so they follow the spine's bend and roll. After the IK solve the hind flippers are turned at
    /// the ankle and toes (FlipperPose) to paddle, square through the push and trailing in the flow through the draw;
    /// the forepaws are turned square through their sweep and edge-on through the recovery, and on the back laid flat on the chest.
    /// Runs after the spine and before the IK solve.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class LimbStrokeAnimator : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("Found on this object when empty; without it the forepaws paddle and hang under the chest standing up as lying on the belly.")]
        public Periscope periscope;
        [Tooltip("The kick on the belly.")]
        public HindKick hindKick = new HindKick();
        [Tooltip("Under water the body lies straight along its way, where on the surface the pushes hold the rump up and the kick with it (SpineKickWave): the kick on the belly tilts round toward the tail as far (deg), so the feet drive along the way as they do there.")]
        public float underwaterTilt = 30f;
        [Tooltip("The kick on the back, under the water: tucked under and ahead of the hip and kicked straight back, every kick a full one.")]
        public HindKick backKick = new HindKick
        {
            tuckedReach = 0.35f, tuckedAngle = -120f, stretchedReach = 0.92f, stretchedAngle = -25f,
            tuckedOut = 0.3f, stretchedOut = 0.35f, kneeBelly = 0.6f, kneeOut = 0.2f, lightAmplitude = 1f,
        };
        public HindTurn hindTurn = new HindTurn();
        public FlukeStroke fluke = new FlukeStroke();
        public HindTread tread = new HindTread();
        public FlipperPose flipper = new FlipperPose();
        public ForePaddle forePaddle = new ForePaddle();
        public ChestRest chestRest = new ChestRest();
        public ForeFollow foreFollow = new ForeFollow();
        public ForeRoll foreRoll = new ForeRoll();
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
            /// <summary>Forepaw: where the wrist lies on the chest on the back (ChestRest), in girdle space; its rest without a chest.</summary>
            public Vector3 chestLocal;
            /// <summary>Forepaw: the chain's end bone and its rest on the forearm; the normal of its inner face (toward the
            /// midline at rest, the palm laid on the chest on the back) and where its fingers point, in the paw's space.</summary>
            public Transform paw;
            public Quaternion pawRest;
            public Vector3 palmLocal, fingersLocal;
            /// <summary>Forepaw: the chest's turn as the water lets the paw follow it (world, ForeFollow) and the chest's
            /// rotation it was taken against, how far the paw trails it (deg), and how much of the paw is in the water (0..1).</summary>
            public Quaternion carried, chestBefore;
            public bool hasCarried;
            public float lag, wet;
            /// <summary>Forepaw: the shoulder blade (the chain root's parent, below the chest) and its rest; null without one.</summary>
            public Transform blade;
            public Quaternion bladeRest;
            /// <summary>Forepaw paddling or in the roll's stroke: where the wrist is going round the arc (world), and how square the paw is to it (ForePaddle.Square).</summary>
            public Vector3 sweep;
            public float square;
            /// <summary>Forepaw: how far it has gone over to its rest on the chest (0..1, ChestRest), and how much of a roll's stroke it is taking (ForeRoll).</summary>
            public float supine, rolling;
            /// <summary>The chain's root joint (shoulder / hip joint) in girdle space.</summary>
            public Vector3 jointLocal;
            public float length;
            /// <summary>Current stroke size, a fraction of the full stroke: the paddle's arc for a forepaw, the kick loop for a hind foot.</summary>
            public float size;
            /// <summary>Hind foot: how far its kick loop swings about the hip's dorso-ventral axis (deg, + = out).</summary>
            public float yaw;
            public AdvancedTwoBoneIK ik;
            /// <summary>Whether the scene limits the chain's mid joint (knee / elbow) about its rest pose.</summary>
            public bool kneeLimited;
            /// <summary>The IK's pole, which this places (a hind leg's always, a forepaw's while it paddles), and where the scene put it.</summary>
            public Transform pole;
            public Vector3 poleRestLocal;

            // hind flipper: the chain's end bone (the foot, hinged at the ankle) and the toe fan below it
            public Transform end, web;
            public Quaternion endRest, webRest;
            /// <summary>Web normal in the flipper's space (the face that is dorsal at rest), and where its toes point.</summary>
            public Vector3 normalLocal, toesLocal;
            /// <summary>The web's pose (toes forward, normal up) in girdle space; its rest in the leg's own frame (LegFrame);
            /// and whether the axis across it at rest (normal × toes) points out to the leg's side (+1) or in (-1).</summary>
            public Quaternion poseLocal, onLeg;
            public float side;
            /// <summary>That pose as the ankle's flex, evert and splay from the web's rest on the leg (deg, Angles), and the
            /// normal's turn from there (deg).</summary>
            public Vector3 ankle;
            public float turned;
            /// <summary>Middle of the web in the flipper's space, and half its width (m) from the skin's spread across it.</summary>
            public Vector3 centerLocal;
            public float webHalfWidth;
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
        /// <summary>The chest and belly the forepaws lie on when on the back: its corners in the chest's space.</summary>
        private Vector3[] chestArea;
        private Transform chestBone;
        private AfterLimbIK afterIK;
        private Water water;
        private DivePath dive;
        private bool started;
        /// <summary>The turn the hind kicks follow, -1..1 of a full turn, + = right.</summary>
        private float turn;
        /// <summary>The treading's beat (cycles, the left foot's; the right runs half a cycle on), and how far toward
        /// its full size and pace the lift asked of it takes it (0..1).</summary>
        private float treadPhase, treadLift;
        /// <summary>The roll over (ForeRoll): the posture it goes to, whether the chest is still on its way there, how far
        /// it has gone (0..1), which side leads (-1 left, +1 right, 0 not yet known), and how much of its stroke is under way.</summary>
        private Posture rollTo;
        private bool rollingOver;
        private float rollProgress = 1f;
        private int rollLead;
        private float rolling;

        public bool HasLimbs => limbs.Any(l => l.valid);

        /// <summary>How far a hind foot's kick loop swings out for the turn (deg); 0 for a forepaw.</summary>
        public float KickYaw(LimbId limb) => limbs[(int)limb].yaw;

        /// <summary>How far the hind legs have gone over from kicking to the fluke (0..1).</summary>
        public float Fluke { get; private set; }
        /// <summary>The treading's beat (cycles from the left foot's top, HindTread).</summary>
        public float TreadBeat => treadPhase;
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

        /// <summary>The chest's roll over from the belly as the spine rolls it, without its bank into a turn (deg, + = right side down): 0 on the belly, ±180 on the back.</summary>
        public float ChestRoll { get; private set; }

        /// <summary>While the chest rolls over, the side whose forepaw takes the roll's stroke (-1 left, +1 right); 0 otherwise.</summary>
        public int RollLead => rollingOver ? rollLead : 0;

        /// <summary>How much of a roll's stroke a forepaw is taking (ForeRoll, 0..1), how far it has gone over to its rest on
        /// the chest (0..1), and the arm's angle about the chest's length in the world, shoulder to wrist from straight down
        /// (deg, the same way round as the chest turns when ChestRoll falls); zeros for a hind foot.</summary>
        public Vector3 ForeRolling(LimbId limb)
        {
            var l = limbs[(int)limb];
            if (!l.valid || !OtterRig.IsFore(limb)) return Vector3.zero;
            Vector3 axis = l.girdle.TransformDirection(l.forwardLocal);
            Vector3 arm = Vector3.ProjectOnPlane(l.ik.end.bone.position - l.ik.root.bone.position, axis);
            return new Vector3(l.rolling, l.supine, Vector3.SignedAngle(Vector3.ProjectOnPlane(-transform.up, axis), arm, axis));
        }

        /// <summary>How far a forepaw trails the chest's turning (deg, ForeFollow), and how much of it is in the water (0..1); zeros for a hind foot.</summary>
        public Vector2 ForeTrail(LimbId limb)
        {
            var l = limbs[(int)limb];
            return OtterRig.IsFore(limb) ? new Vector2(l.lag, l.wet) : Vector2.zero;
        }

        /// <summary>A forepaw's paddle (ForePaddle): the wrist from where the shoulder lies at rest, forward and out to its
        /// side (cm), how far the shoulder blade has taken the shoulder forward (cm), and how far it has taken up the
        /// paddle from rest (0..1); zeros for a hind foot.</summary>
        public Vector4 ForePaddleState(LimbId limb)
        {
            var l = limbs[(int)limb];
            if (!l.valid || !OtterRig.IsFore(limb)) return Vector4.zero;
            KickAxes(l, out var joint, out var back, out var outward, out _);
            Vector3 wrist = l.ik.end.bone.position - joint;
            float reached = -Vector3.Dot(l.ik.root.bone.position - joint, back);
            return new Vector4(-Vector3.Dot(wrist, back) * 100f, Vector3.Dot(wrist, outward) * 100f, reached * 100f, PaddleEngagement(l.size));
        }

        /// <summary>A hind flipper's turn from its rest on the shin as the ankle's flex, evert and splay (deg, Angles); zeros for a forepaw.</summary>
        public Vector3 AnkleAngles(LimbId limb)
        {
            var l = limbs[(int)limb];
            return l.web != null ? l.ankle : Vector3.zero;
        }

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (periscope == null) periscope = GetComponent<Periscope>();
        }

        // limb chains are captured on Start: when added while the scene loads, the IK components are not enabled yet
        private void Start()
        {
            Capture();
            water = FindFirstObjectByType<Water>();
            dive = brain != null && brain.controls != null ? brain.controls.DivePath : null;
            started = true;
            FreeKnees(true);
            afterIK = gameObject.AddComponent<AfterLimbIK>();
            afterIK.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            afterIK.Solved += PoseFlippers;
            afterIK.Solved += PosePaws;
        }

        private void OnEnable()
        {
            if (!started) return;
            FreeKnees(true);
        }

        private void OnDisable()
        {
            RestoreFlippers();
            FreeKnees(false);
            foreach (var l in limbs)
            {
                if (!l.valid) continue;
                if (l.pole != null) l.pole.localPosition = l.poleRestLocal;
                if (l.paw != null && !l.ik.alignEndRotation) l.paw.localRotation = l.pawRest;
                if (l.blade != null) l.blade.localRotation = l.bladeRest;
            }
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
                    chestLocal = rest,
                    currentLocal = rest,
                    forwardLocal = girdle.InverseTransformDirection(transform.forward),
                    upLocal = girdle.InverseTransformDirection(transform.up),
                    jointLocal = girdle.InverseTransformPoint(a),
                    length = Vector3.Distance(a, b) + Vector3.Distance(b, c),
                    ik = ik,
                    kneeLimited = ik.mid.useEulerLimits,
                    pole = ik.pole,
                    poleRestLocal = ik.pole != null ? ik.pole.localPosition : Vector3.zero,
                };
                if (OtterRig.IsFore((LimbId)i)) CapturePaw(ref limbs[i], ik);
                else CaptureFlipper(ref limbs[i], ik);
            }
            if (!HasLimbs) Debug.LogWarning($"[LimbStrokeAnimator] No limb IK chains with targets under '{name}'.", this);
            MeasureChest();
        }

        // The chest and belly the forepaws lie on when on the back: the trunk's skin, all of it but what follows the neck
        // and the forelimbs, from the shoulders back to the hip joint, mapped as the height of its belly side over a grid
        // along the chest's own axes. Each wrist lies out from the midline, as far back from the head as the arm reaches
        // at ease.
        private void MeasureChest()
        {
            chestArea = null;
            var left = limbs[(int)LimbId.FL];
            var right = limbs[(int)LimbId.FR];
            var neck = OtterRig.FindActiveBone(transform, OtterRig.Neck[0]);
            var hip = OtterRig.FindActiveBone(transform, OtterRig.Hip);
            if (!left.valid || !right.valid || left.girdle != right.girdle || neck == null || hip == null) return;
            var chest = left.girdle;
            Vector3 origin = chest.position;
            Vector3 fwd = chest.TransformDirection(left.forwardLocal).normalized;
            Vector3 dorsal = Vector3.ProjectOnPlane(chest.TransformDirection(left.upLocal), fwd).normalized;
            Vector3 side = Vector3.Cross(dorsal, fwd);
            Vector3 Axes(Vector3 w) { w -= origin; return new Vector3(Vector3.Dot(w, fwd), Vector3.Dot(w, side), Vector3.Dot(w, dorsal)); }
            Vector3 Local(Vector3 a) => chest.InverseTransformPoint(origin + fwd * a.x + side * a.y + dorsal * a.z);

            var skipped = new HashSet<Vector3Int>();
            foreach (var bone in new[] { neck, left.ik.root.bone, right.ik.root.bone })
            {
                var skin = OtterRig.FollowingVertices(bone);
                if (skin != null) foreach (var p in skin) skipped.Add(Key(p));
            }
            float front = 0.5f * (Axes(left.ik.root.bone.position).x + Axes(right.ik.root.bone.position).x);
            float back = Axes(hip.position).x;
            var trunk = new List<Vector3>();
            var baked = new Mesh();
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled || skin.sharedMesh == null) continue;
                skin.BakeMesh(baked, true);
                foreach (var v in baked.vertices)
                {
                    Vector3 w = skin.transform.TransformPoint(v);
                    Vector3 a = Axes(w);
                    if (a.x <= front && a.x >= back && !skipped.Contains(Key(w))) trunk.Add(a);
                }
            }
            Destroy(baked);
            if (trunk.Count < 16) return;

            var widths = trunk.Select(a => Mathf.Abs(a.y)).OrderBy(y => y).ToList();
            float half = widths[Mathf.RoundToInt(0.9f * (widths.Count - 1))];
            float cell = half / 6f;
            var belly = new Dictionary<Vector2Int, float>();
            foreach (var a in trunk)
            {
                var k = new Vector2Int(Mathf.FloorToInt(a.x / cell), Mathf.FloorToInt(a.y / cell));
                belly[k] = belly.TryGetValue(k, out var z) ? Mathf.Min(z, a.z) : a.z;
            }
            // the belly side under a point: the lowest skin in its cell and those about it
            bool Under(float x, float y, out float h)
            {
                h = float.MaxValue;
                int i = Mathf.FloorToInt(x / cell), j = Mathf.FloorToInt(y / cell);
                for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                        if (belly.TryGetValue(new Vector2Int(i + di, j + dj), out var z)) h = Mathf.Min(h, z);
                return h < float.MaxValue;
            }

            var r = chestRest;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                if (!OtterRig.IsFore((LimbId)i)) continue;
                ref var l = ref limbs[i];
                Vector3 shoulder = Axes(l.ik.root.bone.position);
                float y = Mathf.Sign(shoulder.y) * r.apart * half;
                for (float x = front; x >= back; x -= 0.25f * cell)
                {
                    // right behind the shoulders the chest's own skin goes with the arms, only the back's is left
                    if (!Under(x, y, out var h) || h > 0f) continue;
                    var wrist = new Vector3(x, y, h - r.lift);
                    if ((wrist - shoulder).magnitude > r.reach * l.length) break;
                    l.chestLocal = Local(wrist);
                }
            }
            float level = Under(0.5f * (front + back), 0f, out var mid) ? mid : 0f;
            chestBone = chest;
            chestArea = new[] { Local(new Vector3(front, -half, level)), Local(new Vector3(front, half, level)), Local(new Vector3(back, half, level)), Local(new Vector3(back, -half, level)) };
        }

        private static Vector3Int Key(Vector3 w) => new Vector3Int(Mathf.RoundToInt(w.x * 1e4f), Mathf.RoundToInt(w.y * 1e4f), Mathf.RoundToInt(w.z * 1e4f));

        // a forepaw on the back lies on the chest; rolling over, it moves between there and its rest under the chest
        private Vector3 RestLocal(int i, in Limb l) =>
            OtterRig.IsFore((LimbId)i) && chestRest.enabled ? Vector3.Lerp(l.restLocal, l.chestLocal, OnChest(l)) : l.restLocal;

        // how far a forepaw has gone to its place on the chest and belly: over on the back, tucked in the dolphin wave,
        // or held there standing up in the water
        private float OnChest(in Limb l) => chestRest.enabled ? Mathf.Max(l.supine, Mathf.Max(Fluke * chestRest.sprintTuck, Upright)) : 0f;

        // how far the otter has stood up in the water (Periscope)
        private float Upright => periscope != null && periscope.isActiveAndEnabled ? periscope.Weight : 0f;

        // how far the hind feet tread water rather than kick (HindTread)
        private float Tread => tread.enabled ? Upright : 0f;

        // The web's normal from the skinned mesh, signed to the face that is dorsal with the leg trailing at rest. Its
        // width runs across the toes.
        private void CaptureFlipper(ref Limb l, AdvancedTwoBoneIK ik)
        {
            var end = ik.end.bone;
            var web = OtterRig.Flipper(end);
            if (web == end || web.childCount == 0 || !OtterRig.TryFootPlane(web, out var normal, out _, out var center, out var spread)) return;
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
            l.toesLocal = web.InverseTransformDirection(toes);
            l.poseLocal = Quaternion.Inverse(l.girdle.rotation) * Quaternion.LookRotation(toes, n);
            l.onLeg = Quaternion.Inverse(LegFrame(l)) * Quaternion.LookRotation(toes, n);
            KickAxes(l, out _, out _, out var outward, out _);
            l.side = Vector3.Dot(Vector3.Cross(n, toes), outward) >= 0f ? 1f : -1f;
            l.centerLocal = center;
            // the spread is one sigma; evenly across a width that is half of it over the square root of three
            l.webHalfWidth = Mathf.Sqrt(3f) * spread.y;
            l.widthAxis = axis;
            l.width = 1f;
        }

        // The paw's flat from the skinned mesh: the face toward the midline, and the fingers toward the toes. The shoulder
        // blade is the bone the arm hangs from below the chest.
        private void CapturePaw(ref Limb l, AdvancedTwoBoneIK ik)
        {
            var blade = ik.root.bone.parent;
            if (blade != null && blade != l.girdle)
            {
                l.blade = blade;
                l.bladeRest = blade.localRotation;
            }
            var paw = ik.end.bone;
            if (paw.childCount == 0 || !OtterRig.TryFootPlane(paw, out var normal, out _, out _, out _)) return;
            Vector3 n = paw.TransformDirection(normal);
            Vector3 medial = l.girdle.position - ik.root.bone.position;
            if (Vector3.Dot(n, Vector3.Project(medial, transform.right)) < 0f) n = -n;
            Vector3 fingers = Vector3.ProjectOnPlane(paw.GetChild(0).position - paw.position, n).normalized;
            l.paw = paw;
            l.pawRest = paw.localRotation;
            l.palmLocal = paw.InverseTransformDirection(n);
            l.fingersLocal = paw.InverseTransformDirection(fingers);
        }

        // Paddling, the paws are set square to their sweep and edge-on through the recovery (Feathered). On the back and
        // tucked in the dolphin wave they lie flat on the chest, palm to it, fingers back along the belly and in toward
        // the midline (ChestRest); rolling over they turn between there and where the forearm carries them.
        private void PosePaws()
        {
            if (!isActiveAndEnabled || brain == null || !brain.HasLimbGeometry) return;
            float inward = chestRest.inward * Mathf.Deg2Rad;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.paw == null) continue;
                if (!l.ik.alignEndRotation) l.paw.localRotation = l.pawRest;
                float engaged = Mathf.Max(PaddleEngagement(l.size), l.rolling);
                if (engaged > 1e-3f && l.sweep.sqrMagnitude > 1e-12f) l.paw.rotation = Quaternion.Slerp(l.paw.rotation, Feathered(l), engaged);
                float supine = OnChest(l) * (1f - l.rolling);
                if (supine < 1e-3f) continue;
                Vector3 fwd = l.girdle.TransformDirection(l.forwardLocal).normalized;
                Vector3 dorsal = Vector3.ProjectOnPlane(l.girdle.TransformDirection(l.upLocal), fwd).normalized;
                Vector3 side = Vector3.Cross(dorsal, fwd);
                Vector3 medial = Vector3.Dot(l.girdle.position - l.girdle.TransformPoint(l.jointLocal), side) > 0f ? side : -side;
                Vector3 fingers = -fwd * Mathf.Cos(inward) + medial * Mathf.Sin(inward);
                Quaternion flat = Quaternion.LookRotation(fingers, dorsal) * Quaternion.Inverse(Quaternion.LookRotation(l.fingersLocal, l.palmLocal));
                l.paw.rotation = Quaternion.Slerp(l.paw.rotation, flat, supine);
            }
        }

        // A paddling paw (ForePaddle): square to its sweep, the palm pressing on the water and the fingers out along the
        // arm; edge-on through the recovery, the fingers leading and the palm toward the body. The wrist turns it only so
        // far from where the forearm carries it.
        private Quaternion Feathered(in Limb l)
        {
            KickAxes(l, out _, out _, out var outward, out _);
            Vector3 sweep = l.sweep.normalized;
            Vector3 along = Vector3.ProjectOnPlane(l.paw.position - l.ik.root.bone.position, sweep);
            Vector3 inward = Vector3.ProjectOnPlane(-outward, sweep);
            if (along.sqrMagnitude < 1e-10f || inward.sqrMagnitude < 1e-10f) return l.paw.rotation;
            Quaternion fromPaw = Quaternion.Inverse(Quaternion.LookRotation(l.fingersLocal, l.palmLocal));
            Quaternion square = Quaternion.LookRotation(along, sweep) * fromPaw;
            Quaternion edgeOn = Quaternion.LookRotation(sweep, inward) * fromPaw;
            return Quaternion.RotateTowards(l.paw.rotation, Quaternion.Slerp(edgeOn, square, l.square), forePaddle.wristRange);
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
            float drive = Mathf.Max(a.active && !a.parking ? a.drive : 0f, b.active && !b.parking ? b.drive : 0f);
            FlukeDrive = Mathf.Lerp(FlukeDrive, drive, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, sizeTime)));
            FlukePhase = FlukePhaseAt(Time.time);
        }

        /// <summary>The fluke's beat at a time from this physics step on to the next (cycles): the hind rhythm's phase,
        /// stepped with physics, run on to it; where it stopped while the hind feet rest.</summary>
        public float FlukePhaseAt(float time)
        {
            if (brain == null) return FlukePhase;
            var a = brain.Rhythm.limbs[(int)LimbId.HL];
            var lead = a.active ? a : brain.Rhythm.limbs[(int)LimbId.HR];
            return lead.active ? Mathf.Repeat(lead.phase + lead.rate * (time - Time.fixedTime), 1f) : FlukePhase;
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
            float lift = periscope != null ? Mathf.Clamp01(periscope.Lift / Mathf.Max(1e-3f, tread.fullLift)) : 0f;
            treadLift = Mathf.Lerp(treadLift, lift, grow);
            treadPhase = Mathf.Repeat(treadPhase + Mathf.Lerp(tread.lightRate, tread.fullRate, treadLift) * Time.deltaTime, 1f);
            Roll();
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid) continue;
                bool fore = OtterRig.IsFore((LimbId)i);
                var st = brain.Rhythm.limbs[i];
                l.size = Mathf.Lerp(l.size, TargetSize(i, l, st), grow);
                if (!fore) l.yaw = KickYaw(l, travel);
                else RollOver(i, ref l);
                float phase = st.active ? Mathf.Repeat(st.phase + st.rate * ahead, 1f) : 0f;
                Vector3 goal = RestLocal(i, l) + l.girdle.InverseTransformVector(StrokeOffset(i, l, st, phase));
                // rolling over, the roll takes the forepaws from any paddle: the leading one into its stroke, the other to its rest
                if (fore && rolling > 0f)
                    goal = Vector3.Lerp(goal, l.rolling > 0f ? l.girdle.InverseTransformPoint(RollWrist(l, rollProgress, out _, out _)) : RestLocal(i, l), rolling);
                l.currentLocal = Vector3.Lerp(l.currentLocal, goal, blend);
                l.target.position = l.girdle.TransformPoint(l.currentLocal);
                if (fore)
                {
                    Paddle(i, ref l, st, phase);
                    Trail(ref l);
                }
                else if (l.pole != null)
                {
                    KickAxes(l, out var joint, out var back, out var outward, out var belly);
                    Underwater(ref back, ref belly);
                    float supine = brain.Posture.SupineFraction;
                    Vector3 knee = joint + l.length * (-back * Mathf.Lerp(hindKick.kneeForward, backKick.kneeForward, supine)
                        + belly * Mathf.Lerp(hindKick.kneeBelly, backKick.kneeBelly, supine) + outward * Mathf.Lerp(hindKick.kneeOut, backKick.kneeOut, supine));
                    if (Tread > 0f)
                    {
                        TreadAxes(i, out var facing, out var side, out _);
                        knee = Vector3.Lerp(knee, joint + l.length * (facing * tread.kneeAhead + side * tread.kneeOut), Tread);
                    }
                    l.pole.position = knee;
                }
                if (l.end != null)
                {
                    l.push = st.active && l.size > 1e-4f ? Set(phase, st.powerFraction) : 0f;
                    if (Tread > 0f) l.push = Mathf.Lerp(l.push, tread.Square(TreadPhase(i)), Tread);
                }
            }
        }

        // how far a hind foot's ankle is set for the push at a phase of its stroke: all through the push until the whip
        // points the foot over its last share, none through the draw
        private float Set(float phase, float powerFraction)
        {
            float whipFrom = powerFraction * (1f - flipper.whip);
            return phase >= powerFraction ? 0f
                : phase <= whipFrom ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f, (phase - whipFrom) / Mathf.Max(1e-4f, powerFraction - whipFrom));
        }

        // The chest's roll over, measured on the chest bone about its own length with its bank into the turn taken out,
        // and how far it has gone toward the posture asked for, short of what the bank leaves (ForeRoll.settled). A roll
        // runs from the posture changing until the chest gets there: the spine eases into a bank after the bank asked
        // for, so in a turn the measure strays by more than that, though nothing rolls over. The side lowest half way
        // over leads: the one the body rolls down onto, whichever way the spine rolls it.
        private void Roll()
        {
            var c = limbs[(int)LimbId.FL].valid ? limbs[(int)LimbId.FL] : limbs[(int)LimbId.FR];
            if (!c.valid) return;
            var r = foreRoll;
            Vector3 axis = c.girdle.TransformDirection(c.forwardLocal);
            Vector3 dorsal = Vector3.ProjectOnPlane(c.girdle.TransformDirection(c.upLocal), axis);
            Vector3 up = Vector3.ProjectOnPlane(transform.up, axis);
            ChestRoll = Mathf.DeltaAngle(brain.Demand.bankTargetDeg, Vector3.SignedAngle(dorsal, up, axis));
            float over = Mathf.Abs(ChestRoll);
            if (brain.Posture.Current != rollTo)
            {
                rollTo = brain.Posture.Current;
                rollingOver = true;
                rollLead = 0;
            }
            float progress = Mathf.Clamp01((rollTo == Posture.Supine ? over : 180f - over) / Mathf.Max(1f, 180f - r.settled));
            if (rollingOver && (progress >= 1f || !brain.Posture.IsFlipping && progress > r.finishAt)) rollingOver = false;
            rollProgress = rollingOver ? progress : 1f;
            if (rollingOver && over > 2f * r.settled && over < 180f - 2f * r.settled) rollLead = ChestRoll > 0f ? 1 : -1;
            float hold = r.enabled && rollingOver && rollLead != 0 ? r.Hold(rollProgress) : 0f;
            rolling = Mathf.Lerp(rolling, hold, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, r.holdTime)));
        }

        // A forepaw's part in the roll over (ForeRoll), and how far over to its rest on the chest it is: the leading paw
        // goes over with the chest, the other gets to its new rest early.
        private void RollOver(int i, ref Limb l)
        {
            bool lead = OtterRig.Side((LimbId)i) == rollLead;
            float over = lead || !foreRoll.enabled ? rollProgress : Mathf.SmoothStep(0f, 1f, rollProgress / foreRoll.settleBy);
            l.supine = rollTo == Posture.Supine ? over : 1f - over;
            l.rolling = lead ? rolling : 0f;
        }

        // The leading forepaw's wrist in the roll's stroke at a share of the roll (ForeRoll.Point), round its shoulder as
        // the shoulder blade carries it, in the chest's own frame
        private Vector3 RollWrist(in Limb l, float progress, out float along, out float fold)
        {
            KickAxes(l, out var joint, out var back, out var outward, out var belly);
            Vector3 at = foreRoll.Point(progress, rollTo == Posture.Supine, out along, out fold);
            Vector3 shoulder = Shoulder(l, joint, back, belly, forePaddle.Shoulder(along) * rolling);
            return shoulder + l.length * (-back * at.x + outward * at.y + belly * at.z);
        }

        // The shoulder blade and the elbow set for a forepaw's paddle (ForePaddle) or its part in a roll over (ForeRoll),
        // as far as the paw has taken them up, and which way the wrist is going round its arc, for the paw's set after the IK
        private void Paddle(int i, ref Limb l, in LimbStroke st, float phase)
        {
            float engaged = PaddleEngagement(l.size);
            KickAxes(l, out var joint, out var back, out var outward, out var belly);
            // rolling over, the leading paw's shoulder blade reaches it out for the catch and draws it back through the
            // sweep; the other's draws the shoulder back and in
            Vector3 rollSweep = Vector3.zero;
            float rollSquare = 0f;
            Vector2 rollBlade = new Vector2(-forePaddle.retract, 0f);
            if (l.rolling > 0f)
            {
                Vector3 at = RollWrist(l, rollProgress, out float along, out float fold);
                rollSweep = RollWrist(l, Mathf.Min(1f, rollProgress + 0.01f), out _, out _) - at;
                rollSquare = 1f - fold;
                rollBlade = forePaddle.Shoulder(along);
            }
            Vector2 o = Vector2.Lerp(forePaddle.Shoulder(ForePaddle.Along(phase, st.powerFraction)) * engaged, rollBlade, rolling);
            Vector3 shoulder = Shoulder(l, joint, back, belly, o);
            if (l.blade != null)
            {
                Vector3 pivot = l.blade.position;
                l.blade.rotation = Quaternion.FromToRotation(joint - pivot, shoulder - pivot) * l.blade.parent.rotation * l.bladeRest;
            }
            if (l.pole != null)
            {
                var p = forePaddle;
                l.pole.localPosition = l.poleRestLocal;
                Vector3 elbow = shoulder + l.length * (back * p.elbowBack - belly * p.elbowUp + outward * p.elbowOut);
                l.pole.position = Vector3.Lerp(l.pole.position, elbow, Mathf.Max(engaged, l.rolling));
            }
            Vector3 sweep = engaged > 0f ? StrokeOffset(i, l, st, Mathf.Repeat(phase + 0.01f, 1f)) - StrokeOffset(i, l, st, phase) : Vector3.zero;
            l.sweep = Vector3.Lerp(sweep, rollSweep, l.rolling);
            l.square = Mathf.Lerp(forePaddle.Square(phase, st.powerFraction), rollSquare, l.rolling);
        }

        // The shoulder as the shoulder blade carries it (ForePaddle.Shoulder: m forward, and up toward the back), e.g.
        // reached forward and up for a catch, drawn back at the finish. It swings on the blade, keeping its distance from
        // where the blade hangs off the chest.
        private static Vector3 Shoulder(in Limb l, Vector3 joint, Vector3 back, Vector3 belly, Vector2 o)
        {
            if (l.blade == null || o == Vector2.zero) return joint;
            Vector3 pivot = l.blade.position, blade = joint - pivot;
            return pivot + (blade - back * o.x - belly * o.y).normalized * blade.magnitude;
        }

        // A forepaw in the water trails the chest's turning (ForeFollow), about the shoulder joint so it keeps its reach:
        // the water holds it where it was while the chest turns, and it eases back to rest. A stroking paw drives itself,
        // as both do through a roll over (ForeRoll); one out of the water or tucked on the chest rides with the chest,
        // keeping how far it trails, so neither going in nor coming out jumps.
        private void Trail(ref Limb l)
        {
            var f = foreFollow;
            Quaternion chest = l.girdle.rotation;
            if (!l.hasCarried) l.carried = l.chestBefore = chest;
            l.hasCarried = true;
            float depth = water != null ? water.Depth(l.target.position) : (0.5f - brain.Posture.SupineFraction) * f.wetDepth;
            l.wet = Mathf.Clamp01(0.5f + depth / Mathf.Max(1e-4f, f.wetDepth));
            float drag = l.wet * (1f - PaddleEngagement(l.size)) * (1f - rolling) * (1f - Mathf.Max(Fluke * chestRest.sprintTuck, Upright));
            Quaternion ride = chest * Quaternion.Inverse(l.chestBefore) * l.carried;
            Quaternion held = Quaternion.Slerp(ride, l.carried, drag);
            l.carried = Quaternion.Slerp(held, chest, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.lagTime)));
            if (!f.enabled) l.carried = chest;
            if (Quaternion.Angle(l.carried, chest) > f.maxLag) l.carried = Quaternion.RotateTowards(chest, l.carried, f.maxLag);
            l.chestBefore = chest;
            Quaternion lag = l.carried * Quaternion.Inverse(chest);
            l.lag = Quaternion.Angle(Quaternion.identity, lag);
            Vector3 joint = l.ik.root.bone.position;
            l.target.position = joint + lag * (l.target.position - joint);
        }

        private void PoseFlippers()
        {
            if (!isActiveAndEnabled || brain == null || !brain.HasLimbGeometry) return;
            var f = flipper;
            float follow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, Mathf.Lerp(f.turnTime, fluke.pitchTime, Fluke)));
            float standing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.standTime));
            float swing = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, f.hingeTime));
            float turnOut = Mathf.Lerp(f.turnOut, f.backTurnOut, brain.Posture.SupineFraction) * Mathf.Deg2Rad;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.web == null) continue;
                // the IK carries the foot rigidly on the shin unless it aligns the end bone itself
                bool ankle = l.ik != null && !l.ik.alignEndRotation;
                if (ankle) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                // how much of a kick the foot is making: none at rest, full from the lightest kick up and treading water
                float kicking = f.enabled ? Mathf.Lerp(KickEngagement(l.size), 1f, Tread) : 0f;
                l.stand = Mathf.Lerp(l.stand, kicking, standing);
                // the web as the shin bone carries it, and the joints' frame: its rest in the plane the leg bends in
                Quaternion webAtRest = l.web.rotation;
                var carriedPose = Quaternion.LookRotation(l.web.TransformDirection(l.toesLocal), l.web.TransformDirection(l.normalLocal));
                Quaternion rest = LegFrame(l) * l.onLeg;
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
                Hinge(i, ref l, flow, turnOut, swing, out var sole, out var toes);
                Vector3 goal = l.stand * Limit(Angles(rest, Quaternion.LookRotation(toes, sole), l.side), l.ankle);
                Quaternion held = l.girdle.rotation * l.poseLocal;
                if (Fluke > 1e-3f) goal = Vector3.Lerp(goal, FlukeAngles(ref l, rest, held * Vector3.up), Fluke);
                else l.flukeFace = 0f;
                // the web held where it was in the hip's space, as the shin now carries the ankle, comes round toward its
                // goal: it keeps its line while the shin swings under it, and nearing the end of a joint's range is carried along
                l.ankle = Vector3.Lerp(Cushion(Angles(rest, held, l.side), l.ankle), goal, follow);
                Quaternion pose = Pose(rest, l.ankle, l.side);
                l.poseLocal = Quaternion.Inverse(l.girdle.rotation) * pose;
                l.turned = Vector3.Angle(rest * Vector3.up, pose * Vector3.up);
                // the ankle hinges its share of the flex, parallel to the knee, and the toe fan takes the rest, so neither
                // joint folds over alone
                if (ankle) l.end.rotation = Quaternion.AngleAxis(f.ankleShare * l.ankle.x, rest * Vector3.right) * l.end.rotation;
                l.web.rotation = pose * Quaternion.Inverse(carriedPose) * webAtRest;

                float kickWidth = Mathf.Lerp(1f, Mathf.Lerp(f.fold, f.spread, l.push), kicking);
                l.width = Mathf.Lerp(l.width, Mathf.Lerp(kickWidth, fluke.width, Fluke), follow);
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
        // flexed. All of it in the kick's frame: swung out with the loop for a turn, square is square to the leg's sweep;
        // treading water, back is the push down.
        // Out: the sole's normal (the face dorsal at rest) and where the toes point.
        private void Hinge(int i, ref Limb l, Vector3 flow, float turnOut, float swing, out Vector3 sole, out Vector3 toes)
        {
            var f = flipper;
            KickAxes(l, out _, out _, out var outward, out var belly);
            LevelAxes(l, out var back, out _);
            var swung = Quaternion.AngleAxis(l.yaw * (1f - Tread), Vector3.Cross(back, outward));
            back = swung * back;
            outward = swung * outward;
            if (Tread > 0f)
            {
                TreadAxes(i, out _, out _, out var push);
                back = Vector3.Slerp(back, push, Tread);
            }
            float outAngle = turnOut * Mathf.Clamp01(l.hinge / 90f);
            Vector3 flex = Vector3.ProjectOnPlane(belly * Mathf.Cos(outAngle) + outward * Mathf.Sin(outAngle), back).normalized;
            float most = f.maxHingeSpeed * Time.deltaTime;
            float step = Mathf.Clamp((90f * l.push - l.hinge) * swing, -most, most);
            float a = l.hinge * Mathf.Deg2Rad;
            Vector3 face = back * Mathf.Sin(a) - flex * Mathf.Cos(a);
            float lever = Mathf.Max(0.01f, Vector3.Distance(l.web.TransformPoint(l.centerLocal), l.end.position));
            float pressed = Vector3.Dot(flow, face) / lever * Mathf.Rad2Deg;
            float caught = f.catchSpeed * l.push * (1f - Mathf.Clamp01(Vector3.Dot(face, back)));
            // treading, the ankle turns the web over on its own (HindTread)
            step = Mathf.Lerp(Mathf.Min(step, (pressed + caught) * Time.deltaTime), step, Tread);
            l.hinge = Mathf.Clamp(l.hinge + step, 0f, 90f);
            a = l.hinge * Mathf.Deg2Rad;
            toes = back * Mathf.Cos(a) + flex * Mathf.Sin(a);
            sole = back * Mathf.Sin(a) - flex * Mathf.Cos(a);
        }

        // A web's pose (toes forward, normal up) against its rest on the shin as the ankle's angles (deg), taken in turn:
        // flex about the axis across the foot (+ = the toes toward the belly side, the normal toward where they pointed:
        // 0 = trailing as at rest, 90 = square), then evert about the foot's length and splay of the toes in the web's
        // own plane, both + toward the leg's side (side: +1 where the axis across the foot points out). Where the web's
        // normal points depends on the flex and evert alone. Pose is the inverse.
        private static Vector3 Angles(Quaternion rest, Quaternion pose, float side)
        {
            Quaternion q = Quaternion.Inverse(rest) * pose;
            Vector3 normal = q * Vector3.up, toes = q * Vector3.forward, across = q * Vector3.right;
            float flex = Mathf.Atan2(normal.z, normal.y) * Mathf.Rad2Deg;
            float evert = Mathf.Asin(Mathf.Clamp(-normal.x, -1f, 1f)) * Mathf.Rad2Deg;
            float splay = Mathf.Atan2(toes.x, across.x) * Mathf.Rad2Deg;
            return new Vector3(flex, -side * evert, side * splay);
        }

        // The leg's own frame at the ankle: forward along the shin, right along the knee's hinge axis, across the plane
        // where the leg bends. The IK may roll the shin bone about its length as it likes; the joints bend from this
        // plane. The IK bends the knee toward its pole, so the plane runs through hip, ankle and pole: still clear as the
        // leg straightens, when the knee's own bend gives no axis.
        private static Quaternion LegFrame(in Limb l)
        {
            Vector3 hip = l.ik.root.bone.position, knee = l.ik.mid.bone.position, ankle = l.end.position;
            Vector3 shin = ankle - knee;
            Vector3 across = l.pole != null ? Vector3.Cross(l.pole.position - hip, ankle - hip) : Vector3.Cross(knee - hip, shin);
            return Quaternion.LookRotation(shin, Vector3.Cross(shin, across));
        }

        private static Quaternion Pose(Quaternion rest, Vector3 angles, float side) =>
            rest * Quaternion.AngleAxis(angles.x, Vector3.right) * Quaternion.AngleAxis(-side * angles.y, Vector3.forward) * Quaternion.AngleAxis(side * angles.z, Vector3.up);

        // The ankle's angles within their ranges (FlipperPose). An angle beyond its range stops at the end it last came
        // from, so a goal swinging round through the far side holds the web there rather than throwing it to the other end.
        private Vector3 Limit(Vector3 angles, Vector3 last)
        {
            var f = flipper;
            return new Vector3(Limit(angles.x, last.x, f.flex), Limit(angles.y, last.y, f.evert), Limit(angles.z, last.z, f.splay));
        }

        private static float Limit(float angle, float last, Vector2 range) =>
            angle >= range.x && angle <= range.y ? angle : Mathf.Clamp(last + Mathf.DeltaAngle(last, angle), range.x, range.y);

        // The web's angles held as the leg swings under it, from where they last were: the closer a joint comes to the end
        // of its range the more of the leg's swing toward that end its stop takes, all of it at the end itself.
        private Vector3 Cushion(Vector3 angles, Vector3 last)
        {
            var f = flipper;
            return new Vector3(Cushion(angles.x, last.x, f.flex, f.cushion), Cushion(angles.y, last.y, f.evert, f.cushion), Cushion(angles.z, last.z, f.splay, f.cushion));
        }

        private static float Cushion(float angle, float last, Vector2 range, float margin)
        {
            float swing = Mathf.DeltaAngle(last, angle);
            float room = Mathf.Abs((swing > 0f ? range.y : range.x) - last);
            float taken = Mathf.SmoothStep(1f, 0f, room / Mathf.Max(1e-3f, margin));
            return Mathf.Clamp(last + swing * (1f - taken), range.x, range.y);
        }

        // How far beyond the ankle's ranges a pose's angles lie, all told (deg).
        private float Beyond(Vector3 angles)
        {
            var f = flipper;
            return Beyond(angles.x, f.flex) + Beyond(angles.y, f.evert) + Beyond(angles.z, f.splay);
        }

        private static float Beyond(float angle, Vector2 range) =>
            angle >= range.x && angle <= range.y ? 0f : Mathf.Min(Mathf.Abs(Mathf.DeltaAngle(angle, range.x)), Mathf.Abs(Mathf.DeltaAngle(angle, range.y)));

        private void RestoreFlippers()
        {
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.valid || l.web == null) continue;
                if (l.ik != null && !l.ik.alignEndRotation) l.end.localRotation = l.endRest;
                l.web.localRotation = l.webRest;
                l.web.localScale = l.webScale;
                l.poseLocal = Quaternion.Inverse(l.girdle.rotation) * Quaternion.LookRotation(l.web.TransformDirection(l.toesLocal), l.web.TransformDirection(l.normalLocal));
                l.ankle = Vector3.zero;
                l.turned = 0f;
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
                ? forePaddle.Amplitude(st.drive) * (1f - Upright)
                : Mathf.Lerp(hindKick.Amplitude(st.drive), backKick.Amplitude(st.drive), brain.Posture.SupineFraction);
        }

        /// <summary>How far a paddle growing in from rest (size 0) or fading out to it has moved onto its arc.</summary>
        private float PaddleEngagement(float size) => Mathf.Clamp01(size / forePaddle.lightAmplitude);

        // the lightest kick's loop size, on the belly or the back or rolling between
        private float LightKick => Mathf.Lerp(hindKick.lightAmplitude, backKick.lightAmplitude, brain.Posture.SupineFraction);

        /// <summary>How far a kick growing in from rest (size 0) or fading out to it has moved onto its loop.</summary>
        private float KickEngagement(float size) => Mathf.Clamp01(size / LightKick);

        /// <summary>World-space paw offset from rest for limb i at a phase of its stroke, at the limb's current size.</summary>
        private Vector3 StrokeOffset(int i, in Limb l, in LimbStroke st, float phase)
        {
            bool fore = OtterRig.IsFore((LimbId)i);
            if (l.size < 1e-5f && (fore || Fluke < 1e-3f && Tread < 1e-3f)) return Vector3.zero;
            Vector3 rest = l.girdle.TransformPoint(l.restLocal);
            KickAxes(l, out var joint, out var back, out var outward, out var belly);
            if (fore)
            {
                // round the shoulder as its blade carries it, in the chest's own frame, so the paddle turns, banks and
                // pitches with the chest; sculling out, the push is straight out from the paw's side, whichever side that is
                var p = forePaddle;
                float engaged = PaddleEngagement(l.size);
                float round = Mathf.Clamp(Mathf.Atan2(-st.direction.x, Mathf.Abs(st.direction.y)) * Mathf.Rad2Deg, -p.maxTurnForward, p.maxTurnBack);
                Vector3 at = p.Point(phase, st.powerFraction, Mathf.Max(l.size, p.lightAmplitude), round);
                Vector3 shoulder = Shoulder(l, joint, back, belly, p.Shoulder(ForePaddle.Along(phase, st.powerFraction)) * engaged);
                Vector3 wrist = shoulder + l.length * (-back * at.x + outward * at.y + belly * at.z);
                return (wrist - rest) * engaged;
            }

            Vector3 offset = Vector3.zero;
            if (l.size >= 1e-5f)
            {
                float amplitude = Mathf.Max(l.size, LightKick), supine = brain.Posture.SupineFraction;
                Vector3 p = Vector3.Lerp(hindKick.Point(hindKick.LoopAt(phase, st.powerFraction), amplitude),
                                         backKick.Point(backKick.LoopAt(phase, st.powerFraction), amplitude), supine);
                Vector3 tuck = Vector3.Lerp(hindKick.Along(amplitude, 0f), backKick.Along(amplitude, 0f), supine);
                Vector3 push = Vector3.Lerp(hindKick.Along(amplitude, 1f), backKick.Along(amplitude, 1f), supine) - tuck;
                // the turn opens the leg out at the hip as it stretches (HindTurn), from none tucked to all of it stretched
                float opened = Mathf.SmoothStep(0f, 1f, Vector3.Dot(p - tuck, push) / Mathf.Max(1e-6f, push.sqrMagnitude));
                float yaw = l.yaw * opened * Mathf.Deg2Rad, cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
                Vector3 kickBack = back, kickBelly = belly;
                Underwater(ref kickBack, ref kickBelly);
                Vector3 swungBack = kickBack * cos + outward * sin, swungOut = outward * cos - kickBack * sin;
                Vector3 ankle = joint + l.length * (swungBack * p.x + swungOut * p.y + kickBelly * p.z);
                offset = (ankle - rest) * KickEngagement(l.size);
            }
            // the kick's loop runs up ahead of the hip, far from the fluke's line behind it, so it gives way early
            if (Fluke > 1e-3f) offset = Vector3.Lerp(offset, FlukeAnkle(l, joint, back, outward, belly) - rest, 1f - (1f - Fluke) * (1f - Fluke));
            if (Tread > 1e-3f) offset = Vector3.Lerp(offset, TreadAnkle(i, l) - rest, Tread);
            // kicking, the feet are held off the midline; as a fluke FlukeStroke places them
            float reach = Mathf.Abs(Vector3.Dot(rest - l.girdle.position, outward)) * hindKick.midlineReach;
            float inward = -Vector3.Dot(offset, outward);
            if (inward > 0f && reach > 1e-4f) offset += outward * ((1f - Fluke) * (inward - reach * (1f - Mathf.Exp(-inward / reach))));
            return offset;
        }

        // Sprinting prone: the leg held out behind along the hips, swinging up and down behind the hips' own pitch, the
        // foot beside the other
        private Vector3 FlukeAnkle(in Limb l, Vector3 joint, Vector3 back, Vector3 outward, Vector3 belly)
        {
            var f = fluke;
            float a = (f.angle - f.sweep * FlukeDrive * Mathf.Cos(2f * Mathf.PI * (FlukePhase - f.lag))) * Mathf.Deg2Rad;
            float side = (l.webHalfWidth * f.width + 0.5f * f.apart - Vector3.Dot(joint - l.girdle.position, outward)) / Mathf.Max(1e-4f, l.length);
            float along = Mathf.Sqrt(Mathf.Max(0f, f.reach * f.reach - side * side));
            return joint + l.length * (along * (back * Mathf.Cos(a) + belly * Mathf.Sin(a)) + outward * side);
        }

        // A hind foot treading water (HindTread): round its ellipse from the top, down the front side through the push
        // and up the back side through the recovery
        private Vector3 TreadAnkle(int i, in Limb l)
        {
            var t = tread;
            TreadAxes(i, out var ahead, out var outward, out var push);
            Vector3 across = Vector3.ProjectOnPlane(ahead, push).normalized;
            Vector3 middle = l.girdle.TransformPoint(l.jointLocal) + l.length * (Vector3.down * t.down + ahead * t.ahead + outward * t.outward);
            float amount = Mathf.Abs(turn), grown = OuterInTurn(i) ? 1f + t.outerGrow * amount : 1f - t.innerShrink * amount;
            float size = Mathf.Lerp(t.lightAmplitude, 1f, treadLift) * grown * l.length, a = 2f * Mathf.PI * TreadPhase(i);
            return middle + size * (-push * (t.depth * Mathf.Cos(a)) + across * (t.width * Mathf.Sin(a)));
        }

        // the treading's beat at a hind foot: the right foot half a cycle on from the left
        private float TreadPhase(int i) => Mathf.Repeat(treadPhase + (OtterRig.Side((LimbId)i) > 0 ? 0.5f : 0f), 1f);

        // whether a hind foot is on the outside of the turn the kicks follow
        private bool OuterInTurn(int i) => OtterRig.Side((LimbId)i) != Math.Sign(turn);

        /// <summary>The treading's frame at a hind foot (world): ahead, level toward where the belly faces; out, level
        /// toward the leg's side; and the push, down and leaning back from ahead, tilted in a turn toward the foot's sweep.</summary>
        private void TreadAxes(int i, out Vector3 ahead, out Vector3 outward, out Vector3 push)
        {
            var t = tread;
            ahead = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            outward = Vector3.Cross(Vector3.up, ahead) * OtterRig.Side((LimbId)i);
            float lean = t.lean * Mathf.Deg2Rad;
            push = Vector3.down * Mathf.Cos(lean) - ahead * Mathf.Sin(lean);
            float amount = Mathf.Abs(turn);
            if (amount < 1e-3f) return;
            bool outer = OuterInTurn(i);
            float swing = (outer ? t.outerSwing : t.innerSwing) * Mathf.Deg2Rad, tilt = t.sweep * amount * Mathf.Deg2Rad;
            Vector3 sweep = outer ? -ahead * Mathf.Cos(swing) + outward * Mathf.Sin(swing) : ahead * Mathf.Cos(swing) - outward * Mathf.Sin(swing);
            push = (push * Mathf.Cos(tilt) + sweep * Mathf.Sin(tilt)).normalized;
        }

        // The fluke pitches the web into the water it meets, from the way it actually moves through it: the hips' own
        // heave and pitch carry the foot as much as the leg's sweep does, and the web whips behind the ankle, so a beat
        // timed from the rhythm alone would pitch it out of step with its path and push as much back as on. Whipped back
        // through the water the web goes by how steeply it moves as well, so as the beat turns over it pitches through
        // level, not over from one stop to the other the moment its slight heave changes sign
        private void FlukePitch(ref Limb l, Vector3 water)
        {
            LevelAxes(l, out var back, out var belly);
            float ahead = -Vector3.Dot(water, back), down = Vector3.Dot(water, belly);
            float path = Mathf.Atan2(down, Mathf.Max(Mathf.Abs(ahead), 0.02f)) * Mathf.Rad2Deg;
            l.flukePitch = Mathf.Clamp(fluke.feather * path, -fluke.attack, fluke.attack);
        }

        // The fluke's web as the ankle's angles: level, pitched into its path, toes trailing straight back along the
        // hips. Sweeping toward the belly its trailing edge rises, so the dorsal face tips forward.
        // Both faces of the web push alike, so it keeps the face it shows as it lies down into the fluke (normal: where
        // it points then): turning over would sweep it face-on through the water ahead. It takes the other only once the
        // shin has carried its face well out of the joints' reach and the other is clearly within it; not as the fluke
        // gives way to the kick again and the web stands back up away from it.
        private Vector3 FlukeAngles(ref Limb l, Quaternion rest, Vector3 normal)
        {
            LevelAxes(l, out var back, out var belly);
            float pitch = l.flukePitch * Mathf.Deg2Rad;
            Vector3 n = -belly * Mathf.Cos(pitch) - back * Mathf.Sin(pitch);
            if (l.flukeFace == 0f) l.flukeFace = Vector3.Dot(normal, n) >= 0f ? 1f : -1f;
            Vector3 toes = Vector3.ProjectOnPlane(-l.girdle.TransformDirection(l.forwardLocal), n);
            if (toes.sqrMagnitude < 1e-6f) toes = Vector3.ProjectOnPlane(rest * Vector3.forward, n);
            Vector3 kept = Angles(rest, Quaternion.LookRotation(toes, n * l.flukeFace), l.side);
            Vector3 other = Angles(rest, Quaternion.LookRotation(toes, -n * l.flukeFace), l.side);
            if (Beyond(other) + 15f < Beyond(kept))
            {
                l.flukeFace = -l.flukeFace;
                kept = other;
            }
            return Limit(kept, l.ankle);
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
        // Under water the heading goes up or down the dive's way at the hips, as the body swims along it.
        // The heading is taken in the hips' up-and-down plane: rolling over, the hips come round after the chest, and
        // their pitch in the world then lies across them, which would turn the webs aside into the tail.
        private void LevelAxes(in Limb l, out Vector3 back, out Vector3 belly)
        {
            KickAxes(l, out _, out var hipBack, out var outward, out var hipBelly);
            Vector3 heading = Vector3.ProjectOnPlane(SwimWay(l), outward);
            back = heading.sqrMagnitude > 0.25f * brain.State.forward.sqrMagnitude && heading.sqrMagnitude > 1e-6f ? -heading.normalized : hipBack;
            Vector3 level = Vector3.ProjectOnPlane(hipBelly, back);
            belly = level.sqrMagnitude > 1e-6f ? level.normalized : hipBelly;
        }

        // a belly kick's back and belly axes tilted round toward the tail by underwaterTilt, as far as the body is on
        // the dive's way
        private void Underwater(ref Vector3 back, ref Vector3 belly)
        {
            float t = dive != null ? dive.Weight * (1f - brain.Posture.SupineFraction) * underwaterTilt * Mathf.Deg2Rad : 0f;
            if (t <= 0f) return;
            float c = Mathf.Cos(t), s = Mathf.Sin(t);
            Vector3 b = back;
            back = b * c - belly * s;
            belly = belly * c + b * s;
        }

        private Vector3 SwimWay(in Limb l)
        {
            Vector3 forward = brain.State.forward;
            ref var fore = ref limbs[(int)LimbId.FL];
            if (dive == null || dive.Weight <= 0f || !fore.valid) return forward;
            Vector3 level = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            return dive.Tangent(level, Vector3.Dot(l.girdle.position - fore.girdle.position, level)) * forward.magnitude;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!drawGizmos || !Application.isPlaying || brain == null || !brain.HasLimbGeometry) return;
            if (chestArea != null && chestBone != null && chestRest.enabled)
            {
                Gizmos.color = new Color(1f, 0.9f, 0.4f, 0.4f + 0.4f * brain.Posture.SupineFraction);
                for (int k = 0; k < chestArea.Length; k++)
                    Gizmos.DrawLine(chestBone.TransformPoint(chestArea[k]), chestBone.TransformPoint(chestArea[(k + 1) % chestArea.Length]));
            }
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var l = limbs[i];
                if (!l.valid) continue;
                var st = brain.Rhythm.limbs[i];
                Color c = OtterRig.IsFore((LimbId)i) ? new Color(0.3f, 0.8f, 1f) : new Color(1f, 0.6f, 0.2f);
                Vector3 rest = l.girdle.TransformPoint(RestLocal(i, l));
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
