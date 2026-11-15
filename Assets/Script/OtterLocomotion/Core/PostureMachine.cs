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
        [Tooltip("Switch posture automatically from how far the arrow is pulled. Manual toggles always work.")]
        public bool autoSwitch = true;
        [Tooltip("Arrow length, as a fraction of the outer ring's radius, that rolls the otter onto its belly to swim fast.")]
        [Range(0f, 1f)] public float proneReach = 0.9f;
        [Tooltip("Pulling the arrow back below this rolls it onto its back again. The gap to proneReach keeps it from " +
                 "flipping back and forth; inside the gap the otter keeps its posture.")]
        [Range(0f, 1f)] public float supineReach = 0.8f;
        [Tooltip("After letting go, or when starting to aim inside supineReach, a prone otter rolls onto its back once slower than this (m/s)...")]
        public float supineEnterSpeed = 0.5f;
        [Tooltip("...for this long (s).")]
        public float supineSettleTime = 0.5f;
        [Tooltip("Nominal roll-over time (s), used until the limbs drive the roll themselves.")]
        public float flipDuration = 0.6f;
    }

    /// <summary>
    /// Prone/supine posture, from how far the player pulls the arrow. Automatic switches fire only when the arrow
    /// crosses a threshold: out past proneReach rolls prone, back inside supineReach rolls supine; letting go, or
    /// starting to aim inside supineReach, rolls supine once slow. A manual toggle holds until the next crossing.
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

        private float previousReach;
        private bool previousSteering;
        private bool hasPrevious;
        private bool pendingSupine;
        private float slowTimer;

        public void Reset(PostureSettings s)
        {
            Current = s.initial;
            FlipProgress = 1f;
            RollSide = 1;
            LastReason = "initial";
            hasPrevious = false;
            pendingSupine = false;
            slowTimer = 0f;
        }

        public void Step(PostureSettings s, in LocomotionIntent intent, in BodyState body, bool togglePressed, float yawRateDemand, float dt)
        {
            float reach = intent.Steering ? intent.reach : 0f;
            if (togglePressed)
            {
                pendingSupine = false;
                Begin(Current == Posture.Prone ? Posture.Supine : Posture.Prone, "manual", yawRateDemand);
            }
            else if (s.autoSwitch)
            {
                int percent = Mathf.RoundToInt(reach * 100f);
                if (hasPrevious)
                {
                    if (reach >= s.proneReach && previousReach < s.proneReach)
                    {
                        pendingSupine = false;
                        Begin(Posture.Prone, $"arrow out to {percent}% of the outer ring", yawRateDemand);
                    }
                    else if (intent.Steering && previousSteering && reach < s.supineReach && previousReach >= s.supineReach)
                    {
                        pendingSupine = false;
                        Begin(Posture.Supine, $"arrow back to {percent}%", yawRateDemand);
                    }
                    else if (intent.Steering != previousSteering && reach < s.supineReach)
                    {
                        // let go, or started aiming close in: roll over once the body has slowed
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
            previousReach = reach;
            previousSteering = intent.Steering;
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
