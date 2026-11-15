using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class PostureSettings
    {
        public Posture initial = Posture.Supine;
        [Tooltip("Top speed on the back (m/s). After rolling onto the back the speed target eases down to this.")]
        public float supineMaxSpeed = 1.2f;
        [Tooltip("Switch posture automatically when the requested speed band changes. Manual toggles always work.")]
        public bool autoSwitch = true;
        [Tooltip("After the player stops asking for speed, a prone otter rolls onto its back once slower than this (m/s)...")]
        public float supineEnterSpeed = 0.5f;
        [Tooltip("...for this long (s).")]
        public float supineSettleTime = 0.5f;
        [Tooltip("Nominal roll-over time (s), used until the limbs drive the roll themselves.")]
        public float flipDuration = 0.6f;
    }

    /// <summary>
    /// Prone/supine posture. Automatic switches fire only on speed band edges (Rest/Aim -> Swim/Sprint rolls prone,
    /// Swim/Sprint -> Rest/Aim rolls supine once slow), so a manual toggle holds until the player changes band.
    /// The speed cap follows the roll progress, and the demand layer eases the speed target down to it.
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

        private SpeedBand previousBand;
        private bool hasPreviousBand;
        private bool pendingSupine;
        private float slowTimer;

        public void Reset(PostureSettings s)
        {
            Current = s.initial;
            FlipProgress = 1f;
            RollSide = 1;
            LastReason = "initial";
            hasPreviousBand = false;
            pendingSupine = false;
            slowTimer = 0f;
        }

        public void Step(PostureSettings s, in LocomotionIntent intent, in BodyState body, bool togglePressed, float yawRateDemand, float dt)
        {
            if (togglePressed)
            {
                pendingSupine = false;
                Begin(Current == Posture.Prone ? Posture.Supine : Posture.Prone, "manual", yawRateDemand);
            }
            else if (s.autoSwitch)
            {
                if (hasPreviousBand && intent.band != previousBand)
                {
                    bool fast = intent.band >= SpeedBand.Swim && intent.requestedSpeed > s.supineMaxSpeed + 1e-3f;
                    if (fast)
                    {
                        pendingSupine = false;
                        Begin(Posture.Prone, $"{previousBand} -> {intent.band}", yawRateDemand);
                    }
                    else if (intent.band <= SpeedBand.Aim)
                    {
                        pendingSupine = Current == Posture.Prone;
                        slowTimer = 0f;
                    }
                }
                if (pendingSupine)
                {
                    slowTimer = body.Speed < s.supineEnterSpeed ? slowTimer + dt : 0f;
                    if (slowTimer >= s.supineSettleTime)
                    {
                        pendingSupine = false;
                        Begin(Posture.Supine, "settled at rest", yawRateDemand);
                    }
                }
            }
            previousBand = intent.band;
            hasPreviousBand = true;
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
