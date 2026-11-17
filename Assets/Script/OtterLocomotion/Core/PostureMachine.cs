using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class PostureSettings
    {
        public Posture initial = Posture.Supine;
        [Tooltip("Top speed on the back between the rings (body lengths/s). After rolling onto the back the speed target eases down to this.")]
        public float supineMaxSpeedBL = 0.8f;
        [Tooltip("How fast the speed target may rise on the back (body lengths/s^2). With proneSprintFraction it sets how " +
                 "long a sprint kicks on the back before rolling over.")]
        public float supineRiseRateBL = 0.6f;
        [Tooltip("Switch posture automatically. Manual toggles always work.")]
        public bool autoSwitch = true;
        [Tooltip("Sprinting (arrow beyond the outer ring) lifts the top speed on the back: the otter speeds up on its back " +
                 "and rolls onto its belly once its speed target reaches this fraction of the sprint speed.")]
        [Range(0f, 1f)] public float proneSprintFraction = 0.55f;
        [Tooltip("Prone, it holds its belly through every turn and slow-down, wherever the arrow goes, and rolls onto its back only once it has swum and then come to a stop: slower than this (body lengths/s)...")]
        public float supineEnterSpeedBL = 0.12f;
        [Tooltip("...for this long (s), long enough that a quick pivot on the spot keeps it prone.")]
        public float supineSettleTime = 1f;
        [Tooltip("Nominal roll-over time (s), used until the limbs drive the roll themselves.")]
        public float flipDuration = 0.6f;
    }

    /// <summary>
    /// Prone/supine posture. Between the rings the otter swims on its back. Sprinting, it first speeds up on its back
    /// and rolls prone once the speed target reaches proneSprintFraction of the sprint speed. Prone it stays prone,
    /// however it turns, slows or steers, until it has swum and then come to a stop; then it rolls onto its back.
    /// Automatic switches fire only on crossings, so a manual toggle holds until the next one. The speed cap follows
    /// the roll progress, and the demand layer eases the speed target down to it.
    /// </summary>
    public sealed class PostureMachine
    {
        /// <summary>The posture being held, or rolled toward while IsFlipping.</summary>
        public Posture Current { get; private set; }
        /// <summary>0..1 through the current roll; 1 when settled.</summary>
        public float FlipProgress { get; private set; } = 1f;
        /// <summary>Which way the body lies when on its back: +1 = rolled over the right side (roll +180).</summary>
        public int RollSide { get; private set; } = 1;
        public string LastReason { get; private set; } = "initial";
        public bool IsFlipping => FlipProgress < 1f;

        /// <summary>0 = fully prone, 1 = fully supine, smooth through a roll.</summary>
        public float SupineFraction
        {
            get
            {
                float s = Mathf.SmoothStep(0f, 1f, FlipProgress);
                return Current == Posture.Supine ? s : 1f - s;
            }
        }

        /// <summary>Nominal body roll (degrees, + = right side down): 0 prone, ±180 supine.</summary>
        public float RollDeg => RollSide * 180f * SupineFraction;

        /// <summary>(new posture, reason) whenever a roll starts.</summary>
        public event Action<Posture, string> Changed;

        private float previousSprint;
        private bool hasPrevious;
        /// <summary>Has swum since it last lay prone at rest, so coming to a stop is a crossing.</summary>
        private bool swam;
        private float slowTimer;

        public void Reset(PostureSettings s)
        {
            Current = s.initial;
            FlipProgress = 1f;
            RollSide = 1;
            LastReason = "initial";
            hasPrevious = false;
            swam = false;
            slowTimer = 0f;
        }

        /// <param name="speedTarget">The demand layer's speed target from the previous step (m/s).</param>
        /// <param name="bodyLength">Metres per body length, the settings' unit of speed.</param>
        public void Step(PostureSettings s, in LocomotionIntent intent, in BodyState body, bool togglePressed, float speedTarget, float yawRateDemand, float bodyLength, float dt)
        {
            // how far a sprint has built up, 0 outside the sprint zone
            float sprint = intent.band == SpeedBand.Sprint ? speedTarget / Mathf.Max(0.1f, intent.requestedSpeed) : 0f;
            if (togglePressed)
            {
                swam = false;
                Begin(Current == Posture.Prone ? Posture.Supine : Posture.Prone, "manual", yawRateDemand);
            }
            else if (s.autoSwitch)
            {
                if (hasPrevious && sprint >= s.proneSprintFraction && previousSprint < s.proneSprintFraction)
                    Begin(Posture.Prone, $"sprint built up to {Mathf.RoundToInt(sprint * 100f)}% of sprint speed", yawRateDemand);
                if (Current == Posture.Prone && !IsFlipping)
                {
                    if (body.Speed >= s.supineEnterSpeedBL * bodyLength)
                    {
                        swam = true;
                        slowTimer = 0f;
                    }
                    else if (swam && (slowTimer += dt) >= s.supineSettleTime)
                    {
                        swam = false;
                        Begin(Posture.Supine, "came to a stop", yawRateDemand);
                    }
                }
                else
                {
                    swam = false;
                    slowTimer = 0f;
                }
            }
            previousSprint = sprint;
            hasPrevious = true;
            if (FlipProgress < 1f) FlipProgress = Mathf.Min(1f, FlipProgress + dt / Mathf.Max(0.05f, s.flipDuration));
        }

        private void Begin(Posture target, string reason, float yawRateDemand)
        {
            if (target == Current) return;
            if (IsFlipping)
            {
                // reverse mid-roll: unwind the way it came, so the roll angle stays continuous
                FlipProgress = 1f - FlipProgress;
            }
            else
            {
                FlipProgress = 0f;
                // roll over the side the body is turning toward, like banking deeper into the turn
                if (target == Posture.Supine) RollSide = yawRateDemand < -1f ? -1 : 1;
            }
            Current = target;
            LastReason = reason;
            Changed?.Invoke(target, reason);
        }
    }
}
