using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class PostureSettings
    {
        [Tooltip("How fast the speed target may rise on the back (body lengths/s^2); on the belly the demand layer's own limit holds.")]
        public float supineRiseRateBL = 0.6f;
        [Tooltip("Nominal roll-over time (s), used until the limbs drive the roll themselves.")]
        public float flipDuration = 0.6f;
    }

    /// <summary>
    /// Prone/supine posture: the controls' gear asks for one (on the back parked and swimming slowly, on the belly
    /// cruising and sprinting) and the body rolls over to it, over the side it is turning toward.
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

        public void Reset(Posture initial)
        {
            Current = initial;
            FlipProgress = 1f;
            RollSide = 1;
            LastReason = "initial";
        }

        /// <param name="reason">Why it is asked for, for the log.</param>
        public void Step(PostureSettings s, Posture wanted, string reason, float yawRateDemand, float dt)
        {
            if (wanted != Current) Begin(wanted, reason, yawRateDemand);
            if (FlipProgress < 1f) FlipProgress = Mathf.Min(1f, FlipProgress + dt / Mathf.Max(0.05f, s.flipDuration));
        }

        private void Begin(Posture target, string reason, float yawRateDemand)
        {
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
