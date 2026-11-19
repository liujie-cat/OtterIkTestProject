using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace OtterLocomotion
{
    [Serializable]
    public class RhythmProfile
    {
        [Tooltip("Strokes per second at the lightest and at full drive.")]
        public float minHz = 1f;
        public float maxHz = 2.2f;
        [Tooltip("Share of the cycle spent pushing water; the rest is the recovery.")]
        [Range(0.2f, 0.8f)] public float powerFraction = 0.45f;
        [Tooltip("Pull toward the pair's phase relation: cycles per second per cycle of phase error.")]
        public float coupling = 3f;

        public RhythmProfile() { }

        public RhythmProfile(float minHz, float maxHz, float powerFraction, float coupling)
        {
            this.minHz = minHz;
            this.maxHz = maxHz;
            this.powerFraction = powerFraction;
            this.coupling = coupling;
        }
    }

    [Serializable]
    public class RhythmSettings
    {
        [Tooltip("Hind feet kicking together: cruising and sprinting on the belly, in the dolphin wave, and a click's kick from rest.")]
        [FormerlySerializedAs("hind")]
        public RhythmProfile hindTogether = new RhythmProfile(0.7f, 3f, 0.3f, 3f);
        [Tooltip("Hind feet taking turns: swimming slowly on the back, and turning on the spot when parked.")]
        public RhythmProfile hindAlternate = new RhythmProfile(0.6f, 2f, 0.4f, 3f);
        public RhythmProfile fore = new RhythmProfile(1.6f, 3.2f, 0.4f, 1.5f);

        [Header("Drive = allocated force / full-drive force")]
        [Tooltip("Allocated force (N) at which a limb strokes at full size and rate.")]
        public float hindFullForce = 2.5f;
        public float foreFullForce = 0.6f;
        [Tooltip("Drive needed to start stroking. Below the release drive a limb finishes its cycle and rests.")]
        public float engageDrive = 0.04f;
        public float releaseDrive = 0.02f;
        [Tooltip("Time constants of the stroke strength following the allocation (s).")]
        public float driveRiseTime = 0.04f;
        public float driveFallTime = 0.2f;

        [Header("Hurried recovery: a fresh demand brings the next power stroke on early")]
        [Tooltip("Last part of the recovery, as a fraction of it, which a fresh demand hurries through.")]
        [Range(0f, 1f)] public float resetWindow = 0.5f;
        [Tooltip("Time a fresh demand takes to draw the foot through that window (s). Not a jump: snapping the leg forward would whip the web through the water and brake the body.")]
        public float resetTime = 0.15f;
        [Tooltip("Rise in drive over the current stroke's, or turn of the stroke direction, that counts as a fresh demand.")]
        public float resetDriveJump = 0.25f;
        public float resetAngleDeg = 60f;
        [Tooltip("A limb joining a stroking partner waits for its place in the pair's rhythm only if that comes this soon (s); otherwise it pushes at once.")]
        public float maxJoinDelay = 0.15f;
    }

    /// <summary>One limb's stroke cycle. Phase 0..powerFraction is the power stroke, the rest the recovery.</summary>
    public struct LimbStroke
    {
        public bool active;
        /// <summary>No longer needed: finishing the current cycle, then resting.</summary>
        public bool parking;
        public float phase;
        /// <summary>Phase rate including the pair coupling (cycles/s).</summary>
        public float rate;
        public float powerFraction;
        /// <summary>Stroke strength 0..1, following the allocation.</summary>
        public float drive;
        public float targetDrive;
        /// <summary>
        /// Paw motion relative to the body in this stroke's power phase, body frame (x forward, y right), unit length:
        /// opposite to the force the stroke puts on the body. Fixed for the whole stroke.
        /// </summary>
        public Vector2 direction;
        public Vector2 targetDirection;
        /// <summary>Drive when this stroke began, for detecting a fresh demand.</summary>
        public float strokeDrive;
        public int strokes;
        /// <summary>Strength of a click's kick waiting for this limb's next power stroke, or driving this one; 0 without one.</summary>
        public float kick;
        /// <summary>This power stroke is a click's kick.</summary>
        public bool kicking;
        public bool InPower => active && phase < powerFraction;
    }

    /// <summary>
    /// Per-limb phase oscillators (a small central pattern generator) driven by the allocation: how hard a limb is
    /// asked to push sets its stroke strength and rate, the force direction sets its stroke direction, fixed when each
    /// power phase begins. Left and right of a girdle pull toward alternating strokes; the hind feet kick together in
    /// the gaits on the belly. A limb with nothing to do finishes its cycle and rests; a fresh demand in the late
    /// recovery hurries the rest of it through, and a limb starting from rest pushes immediately. A click's kick
    /// (Kick) makes the next power stroke of both hind feet at least that strong and straight back; from rest they
    /// draw up and kick it together, once.
    /// </summary>
    public sealed class LimbRhythm
    {
        public readonly LimbStroke[] limbs = new LimbStroke[OtterRig.LimbCount];
        public event Action<LimbId, LimbStroke> PowerStarted;
        /// <summary>Hind feet kicking together (the belly gaits, or a kick from rest) rather than taking turns.</summary>
        public bool HindTogether { get; private set; }

        private static readonly Vector2 Back = new Vector2(-1f, 0f);
        private readonly bool[] want = new bool[OtterRig.LimbCount];
        private readonly bool[] startedNow = new bool[OtterRig.LimbCount];
        private readonly float[] stepPhase = new float[OtterRig.LimbCount];
        /// <summary>Both hind feet were at rest when a kick came, so they kick it together whatever the gait.</summary>
        private bool kickFromRest;

        public void Reset()
        {
            Array.Clear(limbs, 0, limbs.Length);
            HindTogether = false;
            kickFromRest = false;
        }

        private static int Partner(int limb) => limb ^ 1;
        private static bool IsLeft(int limb) => (limb & 1) == 0;
        private static float Wrap(float x) => x - Mathf.Round(x);

        /// <summary>A click's kick for the hind feet, strength 0..1; taken up on the next Step.</summary>
        public void Kick(float strength)
        {
            int hl = (int)LimbId.HL, hr = (int)LimbId.HR;
            if (!limbs[hl].active && !limbs[hr].active) kickFromRest = true;
            foreach (int i in new[] { hl, hr })
            {
                limbs[i].kick = Mathf.Max(limbs[i].kick, strength);
                limbs[i].kicking = false;
            }
        }

        /// <param name="gait">The controls' hind gait: together on the belly, taking turns on the back.</param>
        public void Step(RhythmSettings s, ControlAllocator allocation, Gait gait, float dt)
        {
            ref var hindL = ref limbs[(int)LimbId.HL];
            ref var hindR = ref limbs[(int)LimbId.HR];
            // parked, the kick's pair recovers together and comes to rest together
            if (hindL.kick <= 0f && hindR.kick <= 0f && (gait != Gait.Rest || (!hindL.active && !hindR.active))) kickFromRest = false;
            HindTogether = gait == Gait.ProneTogether || gait == Gait.Dolphin || kickFromRest;

            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                Vector2 force = allocation.limbs[i].force;
                float full = OtterRig.IsFore((LimbId)i) ? s.foreFullForce : s.hindFullForce;
                float magnitude = force.magnitude;
                l.targetDrive = full > 1e-4f ? Mathf.Clamp01(magnitude / full) : 0f;
                if (magnitude > 1e-4f) l.targetDirection = -force / magnitude;
                else if (l.targetDirection == Vector2.zero) l.targetDirection = Back;
                // a kick pushes straight back, unless the allocation asks for a harder stroke of its own
                if (l.kick > l.targetDrive)
                {
                    l.targetDrive = l.kick;
                    l.targetDirection = Back;
                }

                want[i] = l.kick > 0f || l.targetDrive >= (l.active && !l.parking ? s.releaseDrive : s.engageDrive);
                float goal = want[i] ? l.targetDrive : 0f;
                float tau = goal > l.drive ? s.driveRiseTime : s.driveFallTime;
                l.drive += (goal - l.drive) * (1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, tau)));
                startedNow[i] = false;
            }

            // limbs starting from rest; the harder-pushed side of a pair goes first
            for (int pass = 0; pass < 2; pass++)
            {
                for (int pair = 0; pair < OtterRig.LimbCount; pair += 2)
                {
                    int first = limbs[pair].targetDrive >= limbs[pair + 1].targetDrive ? pair : pair + 1;
                    int i = pass == 0 ? first : Partner(first);
                    if (!limbs[i].active && want[i]) Start(s, i);
                }
            }

            // a pair couples on the phases both had at the start of the step: coupling the second on the first's new
            // phase, a pair half a cycle apart would each be pushed back across the half and both slow, locked apart
            for (int i = 0; i < OtterRig.LimbCount; i++) stepPhase[i] = limbs[i].phase;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.active) { l.rate = 0f; continue; }
                l.parking = !want[i];
                var profile = Profile(s, i);
                float rate = Mathf.Lerp(profile.minHz, profile.maxHz, l.drive);
                if (limbs[Partner(i)].active)
                    rate += profile.coupling * Wrap(stepPhase[Partner(i)] - stepPhase[i] - PartnerLead(i));
                rate = Mathf.Max(0.2f * profile.minHz, rate);

                float window = (1f - l.powerFraction) * s.resetWindow;
                bool freshKick = l.kick > 0f && !l.kicking;
                if (!l.parking && !startedNow[i] && l.phase >= l.powerFraction && l.phase >= 1f - window &&
                    (freshKick || l.targetDrive > l.strokeDrive + s.resetDriveJump || Vector2.Angle(l.targetDirection, l.direction) > s.resetAngleDeg))
                    rate = Mathf.Max(rate, window / Mathf.Max(1e-3f, s.resetTime));
                l.phase += rate * dt;
                l.rate = rate;
                if (l.kicking && l.phase >= l.powerFraction)
                {
                    l.kick = 0f;
                    l.kicking = false;
                }

                if (l.phase < 1f) continue;
                l.phase -= Mathf.Floor(l.phase);
                if (l.parking)
                {
                    l.active = false;
                    l.parking = false;
                    l.phase = 0f;
                    l.rate = 0f;
                }
                else BeginStroke(s, i);
            }
        }

        private RhythmProfile Profile(RhythmSettings s, int limb) =>
            OtterRig.IsFore((LimbId)limb) ? s.fore : HindTogether ? s.hindTogether : s.hindAlternate;

        /// <summary>How far the partner's phase should run ahead of this limb's (cycles).</summary>
        private float PartnerLead(int limb)
        {
            bool together = !OtterRig.IsFore((LimbId)limb) && HindTogether;
            float rightAhead = together ? 0f : 0.5f;
            return IsLeft(limb) ? rightAhead : -rightAhead;
        }

        private void Start(RhythmSettings s, int i)
        {
            ref var l = ref limbs[i];
            ref var p = ref limbs[Partner(i)];
            l.powerFraction = Profile(s, i).powerFraction;
            // a kick from rest draws the foot up through the late recovery first: pushing straight from the rest pose
            // the stroke's loop grows out ahead of the foot and sweeps the web forward, backing the body
            float phase = l.kick > 0f ? 1f - (1f - l.powerFraction) * s.resetWindow : 0f;
            if (p.active)
            {
                float slot = Mathf.Repeat(p.phase - PartnerLead(i), 1f);
                float rate = Mathf.Max(0.1f, Mathf.Lerp(Profile(s, i).minHz, Profile(s, i).maxHz, l.targetDrive));
                bool soon = slot < 0.5f * l.powerFraction || (1f - slot) / rate <= s.maxJoinDelay;
                // a pair starting together has to split up to alternate, so the second one always takes its slot
                if (soon || startedNow[Partner(i)]) phase = slot;
            }
            l.active = true;
            l.parking = false;
            l.phase = phase;
            startedNow[i] = true;
            l.direction = l.targetDirection;
            l.strokeDrive = l.targetDrive;
            if (phase < l.powerFraction) BeginStroke(s, i);
        }

        // a stroke keeps the split it began with: changing gait mid-stroke would throw a recovering foot back into its push
        private void BeginStroke(RhythmSettings s, int i)
        {
            ref var l = ref limbs[i];
            l.powerFraction = Profile(s, i).powerFraction;
            l.direction = l.targetDirection;
            l.strokeDrive = l.targetDrive;
            l.kicking = l.kick > 0f;
            l.strokes++;
            PowerStarted?.Invoke((LimbId)i, l);
        }
    }
}
