using System;
using UnityEngine;

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
        public RhythmProfile hind = new RhythmProfile(1f, 2.2f, 0.45f, 3f);
        public RhythmProfile fore = new RhythmProfile(1.6f, 3.2f, 0.4f, 1.5f);

        [Header("Drive = allocated force / the limb's strongest stroke")]
        [Tooltip("Drive needed to start stroking. Below the release drive a limb finishes its cycle and rests.")]
        public float engageDrive = 0.08f;
        public float releaseDrive = 0.04f;
        [Tooltip("Time constants of the stroke strength following the allocation (s).")]
        public float driveRiseTime = 0.04f;
        public float driveFallTime = 0.2f;

        [Header("Phase reset: a fresh demand starts the next power stroke early")]
        [Tooltip("Last part of the recovery, as a fraction of it, in which a fresh demand starts the power stroke at once.")]
        [Range(0f, 1f)] public float resetWindow = 0.5f;
        [Tooltip("Rise in drive over the current stroke's, or turn of the stroke direction, that counts as a fresh demand.")]
        public float resetDriveJump = 0.25f;
        public float resetAngleDeg = 60f;
        [Tooltip("A limb joining a stroking partner waits for its place in the pair's rhythm only if that comes this soon (s); otherwise it pushes at once.")]
        public float maxJoinDelay = 0.15f;
        [Tooltip("Hind feet paddle alternately when cruising and together when sprinting (Williams 1989).")]
        public bool hindTogetherWhenSprinting = true;
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
        public bool InPower => active && phase < powerFraction;
    }

    /// <summary>
    /// Per-limb phase oscillators (a small central pattern generator) driven by the allocation: how hard a limb is
    /// asked to push sets its stroke strength and rate, the force direction sets its stroke direction, fixed when each
    /// power phase begins. Left and right of a girdle pull toward alternating strokes (hind feet together when
    /// sprinting). A limb with nothing to do finishes its cycle and rests; a fresh demand in the late recovery starts
    /// the next power stroke at once, and a limb starting from rest pushes immediately.
    /// </summary>
    public sealed class LimbRhythm
    {
        public readonly LimbStroke[] limbs = new LimbStroke[OtterRig.LimbCount];
        public event Action<LimbId, LimbStroke> PowerStarted;

        private readonly bool[] want = new bool[OtterRig.LimbCount];
        private readonly bool[] startedNow = new bool[OtterRig.LimbCount];

        public void Reset() => Array.Clear(limbs, 0, limbs.Length);

        private static int Partner(int limb) => limb ^ 1;
        private static bool IsLeft(int limb) => (limb & 1) == 0;
        private static float Wrap(float x) => x - Mathf.Round(x);

        public void Step(RhythmSettings s, ControlAllocator allocation, ActuatorModel model, SpeedBand band, float dt)
        {
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                l.powerFraction = Profile(s, i).powerFraction;
                Vector2 force = allocation.limbs[i].force;
                float strongest = 0f;
                for (int k = 0; k < ActuatorModel.ColumnsPerLimb; k++)
                    strongest = Mathf.Max(strongest, model.columns[i * ActuatorModel.ColumnsPerLimb + k].hi);
                float magnitude = force.magnitude;
                l.targetDrive = strongest > 1e-4f ? Mathf.Clamp01(magnitude / strongest) : 0f;
                if (magnitude > 1e-4f) l.targetDirection = -force / magnitude;
                else if (l.targetDirection == Vector2.zero) l.targetDirection = new Vector2(-1f, 0f);

                want[i] = l.targetDrive >= (l.active && !l.parking ? s.releaseDrive : s.engageDrive);
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
                    if (!limbs[i].active && want[i]) Start(s, i, band);
                }
            }

            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                ref var l = ref limbs[i];
                if (!l.active) { l.rate = 0f; continue; }
                l.parking = !want[i];
                var profile = Profile(s, i);
                float rate = Mathf.Lerp(profile.minHz, profile.maxHz, l.drive);
                ref var p = ref limbs[Partner(i)];
                if (p.active)
                    rate += profile.coupling * Wrap(p.phase - l.phase - PartnerLead(s, i, band));
                rate = Mathf.Max(0.2f * profile.minHz, rate);

                if (!l.parking && !startedNow[i] && l.phase >= l.powerFraction &&
                    l.phase >= 1f - (1f - l.powerFraction) * s.resetWindow &&
                    (l.targetDrive > l.strokeDrive + s.resetDriveJump || Vector2.Angle(l.targetDirection, l.direction) > s.resetAngleDeg))
                    l.phase = 1f;
                else
                    l.phase += rate * dt;
                l.rate = rate;

                if (l.phase < 1f) continue;
                l.phase -= Mathf.Floor(l.phase);
                if (l.parking)
                {
                    l.active = false;
                    l.parking = false;
                    l.phase = 0f;
                    l.rate = 0f;
                }
                else BeginStroke(i);
            }
        }

        private static RhythmProfile Profile(RhythmSettings s, int limb) => OtterRig.IsFore((LimbId)limb) ? s.fore : s.hind;

        /// <summary>How far the partner's phase should run ahead of this limb's (cycles).</summary>
        private static float PartnerLead(RhythmSettings s, int limb, SpeedBand band)
        {
            bool together = !OtterRig.IsFore((LimbId)limb) && s.hindTogetherWhenSprinting && band == SpeedBand.Sprint;
            float rightAhead = together ? 0f : 0.5f;
            return IsLeft(limb) ? rightAhead : -rightAhead;
        }

        private void Start(RhythmSettings s, int i, SpeedBand band)
        {
            ref var l = ref limbs[i];
            ref var p = ref limbs[Partner(i)];
            float phase = 0f;
            if (p.active)
            {
                float slot = Mathf.Repeat(p.phase - PartnerLead(s, i, band), 1f);
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
            if (phase < l.powerFraction) BeginStroke(i);
        }

        private void BeginStroke(int i)
        {
            ref var l = ref limbs[i];
            l.direction = l.targetDirection;
            l.strokeDrive = l.targetDrive;
            l.strokes++;
            PowerStarted?.Invoke((LimbId)i, l);
        }
    }
}
