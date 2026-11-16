using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class PostureSettings
    {
        public Posture initial = Posture.Supine;
        [Tooltip("Top speed on the back (m/s) between the rings. After rolling onto the back the speed target eases down to this.")]
        public float supineMaxSpeed = 1.2f;
        [Tooltip("How fast the speed target may rise on the back (m/s^2). With proneSprintFraction it sets how long a " +
                 "sprint kicks on the back before rolling over.")]
        public float supineRiseRate = 2.5f;
        [Tooltip("Switch posture automatically. Manual toggles always work.")]
        public bool autoSwitch = true;
        [Tooltip("Sprinting (arrow beyond the outer ring) lifts the top speed on the back: the otter speeds up on its back " +
                 "and rolls onto its belly once its speed target reaches this fraction of the sprint speed.")]
        [Range(0f, 1f)] public float proneSprintFraction = 0.9f;
        [Tooltip("Pulling the arrow back below this fraction of the outer ring rolls it onto its back again. The gap to " +
                 "the outer ring keeps it from flipping back and forth at the edge of the sprint zone.")]
        [Range(0f, 1f)] public float supineReach = 0.9f;
        [Tooltip("After letting go, or when starting to aim inside supineReach, a prone otter rolls onto its back once slower than this (m/s)...")]
        public float supineEnterSpeed = 0.5f;
        [Tooltip("...for this long (s).")]
        public float supineSettleTime = 0.5f;
        [Tooltip("Nominal roll-over time (s), used until the limbs drive the roll themselves.")]
        public float flipDuration = 0.6f;
    }

    /// <summary>
    /// Prone/supine posture. Between the rings the otter swims on its back. Sprinting, it first speeds up on its back
    /// and rolls prone once the speed target reaches proneSprintFraction of the sprint speed; pulling the arrow back
    /// inside supineReach rolls it supine, and letting go, or starting to aim, rolls it supine once slow. Automatic
    /// switches fire only on crossings, so a manual toggle holds until the next one. The speed cap follows the roll
    /// progress, and the demand layer eases the speed target down to it.
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

        private float previousReach, previousSprint;
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

        /// <param name="speedTarget">The demand layer's speed target from the previous step (m/s).</param>
        public void Step(PostureSettings s, in LocomotionIntent intent, in BodyState body, bool togglePressed, float speedTarget, float yawRateDemand, float dt)
        {
            float reach = intent.Steering ? intent.reach : 0f;
            // how far a sprint has built up, 0 outside the sprint zone
            float sprint = intent.band == SpeedBand.Sprint ? speedTarget / Mathf.Max(0.1f, intent.requestedSpeed) : 0f;
            if (togglePressed)
            {
                pendingSupine = false;
                Begin(Current == Posture.Prone ? Posture.Supine : Posture.Prone, "manual", yawRateDemand);
            }
            else if (s.autoSwitch)
            {
                if (hasPrevious)
                {
                    if (sprint >= s.proneSprintFraction && previousSprint < s.proneSprintFraction)
                    {
                        pendingSupine = false;
                        Begin(Posture.Prone, $"sprint built up to {Mathf.RoundToInt(sprint * 100f)}% of sprint speed", yawRateDemand);
                    }
                    else if (intent.Steering && previousSteering && reach < s.supineReach && previousReach >= s.supineReach)
                    {
                        pendingSupine = false;
                        Begin(Posture.Supine, $"arrow back to {Mathf.RoundToInt(reach * 100f)}% of the outer ring", yawRateDemand);
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
            previousSprint = sprint;
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
