using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace OtterLocomotion
{
    /// <summary>Scull out = the paw sweeps away from the body's midline (pushing the body toward it); scull in = across it.</summary>
    public enum StrokeKind { Thrust, Brake, ScullOut, ScullIn }

    /// <summary>Where a limb pushes from, measured on the rig in the prone body frame (x forward, y right, z up).</summary>
    public struct LimbGeometry
    {
        public bool valid;
        /// <summary>Shoulder / hip joint, metres from the body origin.</summary>
        public Vector3 anchor;
        /// <summary>Upper + lower limb length (m).</summary>
        public float length;
    }

    [Serializable]
    public class StrokeCapacity
    {
        [Tooltip("Cycle-averaged force limit, as an acceleration of the whole body (m/s^2); times mass = newtons.")]
        public float maxAccel = 1f;
        [Tooltip("Relative effort; for the same result the allocator prefers cheaper strokes.")]
        public float cost = 1f;
        [Tooltip("Effectiveness lying prone (0..1).")]
        [Range(0f, 1f)] public float prone = 1f;
        [Tooltip("Effectiveness lying on the back (0..1); blended through a roll.")]
        [Range(0f, 1f)] public float supine = 0.5f;

        public StrokeCapacity() { }

        public StrokeCapacity(float maxAccel, float cost, float prone, float supine)
        {
            this.maxAccel = maxAccel;
            this.cost = cost;
            this.prone = prone;
            this.supine = supine;
        }
    }

    [Serializable]
    public class ActuatorSettings
    {
        [Header("Hind feet: main propulsion")]
        public StrokeCapacity hindThrust = new StrokeCapacity(4f, 1f, 1f, 0.7f);
        public StrokeCapacity hindBrake = new StrokeCapacity(2f, 1.5f, 1f, 0.6f);
        [FormerlySerializedAs("hindScull")]
        public StrokeCapacity hindScullOut = new StrokeCapacity(1.5f, 2f, 1f, 0.8f);
        public StrokeCapacity hindScullIn = new StrokeCapacity(0.6f, 4f, 1f, 0.8f);

        [Header("Forepaws: steering and fine sculling when prone, little thrust; held on the chest out of the water on the back")]
        [Tooltip("Turning on the spot prone the forepaws scull every way; as the speed target passes this (body lengths/s) they fold against the chest.")]
        public float foreTuckSpeedBL = 0.15f;
        [Tooltip("Cruising prone the paw on the outside of a turn paddles it round, sculling out like a single oar, from a turn asked of foreSteerFrom (deg/s) to all it can at foreSteerFull. The inside paw stays folded, and both do going straight or sprinting in the dolphin wave.")]
        public float foreSteerFrom = 15f;
        public float foreSteerFull = 60f;
        public StrokeCapacity foreThrust = new StrokeCapacity(0.5f, 12f, 1f, 0f);
        public StrokeCapacity foreBrake = new StrokeCapacity(1.5f, 1.5f, 1f, 0f);
        [Tooltip("Sweeping out to the side is the strong scull; sweeping in across the chest is weak, so the paw on the outside of a turn sculls.")]
        [FormerlySerializedAs("foreScull")]
        public StrokeCapacity foreScullOut = new StrokeCapacity(2f, 0.5f, 1f, 0f);
        public StrokeCapacity foreScullIn = new StrokeCapacity(0.8f, 1.5f, 1f, 0f);

        [Header("Where the paw works, as a fraction of the limb length from the shoulder / hip")]
        public float hindReach = 0.5f;
        public float hindDepth = 0.5f;
        public float foreReach = 0.4f;
        public float foreDepth = 0.3f;

        [Tooltip("Share of effectiveness lost at the middle of a roll-over: limbs busy rolling push little water.")]
        [Range(0f, 1f)] public float rollPenalty = 0.3f;
    }

    /// <summary>One stroke primitive: the body-frame wrench (Fx, Fy, Mz) per newton of stroke force, and its limits.</summary>
    public struct StrokeColumn
    {
        public LimbId limb;
        public StrokeKind kind;
        public Vector3 wrench;
        public float lo, hi;
        /// <summary>Effort cost already divided by effectiveness: a half-wet paw costs double for the same force.</summary>
        public float cost;
        public float effectiveness;
    }

    /// <summary>
    /// The limbs as force generators for the current posture. Rebuilt every step from the rig geometry, rolled by the
    /// posture's roll angle, so prone/supine/mid-roll lever arms and side swaps fall out of the geometry.
    /// Moments are about the girdle centre (the body's centre of mass in this model).
    /// </summary>
    public sealed class ActuatorModel
    {
        public const int ColumnsPerLimb = 4;

        public readonly StrokeColumn[] columns = new StrokeColumn[OtterRig.LimbCount * ColumnsPerLimb];
        /// <summary>Stroke centre of each limb in the current (rolled) body frame, metres from the body origin.</summary>
        public readonly Vector3[] points = new Vector3[OtterRig.LimbCount];
        public readonly bool[] present = new bool[OtterRig.LimbCount];
        public Vector3 CenterOfMass { get; private set; }

        /// <param name="speedTargetBL">The demand layer's speed target (body lengths/s), which folds the forepaws away when swimming prone.</param>
        /// <param name="yawRateTarget">The demand layer's turn (deg/s): cruising, the forepaws paddle it round.</param>
        /// <param name="cruising">Swimming prone short of the dolphin wave, where the forepaws steer.</param>
        public void Build(ActuatorSettings s, LimbGeometry[] geometry, Vector3 centerOfMass, float rollDeg, float supineFraction, float mass, float speedTargetBL, float yawRateTarget, bool cruising)
        {
            CenterOfMass = centerOfMass;
            float roll = rollDeg * Mathf.Deg2Rad, cos = Mathf.Cos(roll), sin = Mathf.Sin(roll);
            float rollFactor = 1f - s.rollPenalty * Mathf.Sin(Mathf.PI * Mathf.Clamp01(supineFraction));
            float foreOut = 1f - Mathf.Clamp01(speedTargetBL / Mathf.Max(1e-3f, s.foreTuckSpeedBL));
            float steer = cruising ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(s.foreSteerFrom, s.foreSteerFull, Mathf.Abs(yawRateTarget))) : 0f;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var limb = (LimbId)i;
                bool fore = OtterRig.IsFore(limb);
                var g = geometry[i];
                present[i] = g.valid;
                float reach = (fore ? s.foreReach : s.hindReach) * g.length;
                float depth = (fore ? s.foreDepth : s.hindDepth) * g.length;
                Vector3 p = g.anchor + new Vector3(0f, OtterRig.Side(limb) * reach, -depth) - centerOfMass;
                // roll about the forward axis, + = right side down
                p = new Vector3(p.x, p.y * cos + p.z * sin, -p.y * sin + p.z * cos);
                points[i] = p + centerOfMass;
                // the side the limb is on now: belly up, the left limbs are on the right
                float side = Mathf.Abs(p.y) > 1e-4f ? Mathf.Sign(p.y) : OtterRig.Side(limb);

                // prone, how much of its use a forepaw has: all of it turning on the spot, only sculling out on the outside
                // of a cruise turn (to the left the right paw, on the side away from it), not even checking the turn as it eases
                float use = fore ? foreOut : 1f;
                float oar = fore && side * yawRateTarget < 0f ? Mathf.Max(use, steer) : use;
                SetColumn(i, 0, limb, StrokeKind.Thrust, new Vector3(1f, 0f, -p.y), fore ? s.foreThrust : s.hindThrust, use);
                SetColumn(i, 1, limb, StrokeKind.Brake, new Vector3(-1f, 0f, p.y), fore ? s.foreBrake : s.hindBrake, use);
                SetColumn(i, 2, limb, StrokeKind.ScullOut, new Vector3(0f, -side, -side * p.x), fore ? s.foreScullOut : s.hindScullOut, oar);
                SetColumn(i, 3, limb, StrokeKind.ScullIn, new Vector3(0f, side, side * p.x), fore ? s.foreScullIn : s.hindScullIn, use);
            }

            void SetColumn(int limbIndex, int k, LimbId limb, StrokeKind kind, Vector3 wrench, StrokeCapacity cap, float proneUse)
            {
                float e = present[limbIndex] ? Mathf.Lerp(cap.prone * proneUse, cap.supine, supineFraction) * rollFactor : 0f;
                float hi = cap.maxAccel * mass * e;
                columns[limbIndex * ColumnsPerLimb + k] = new StrokeColumn
                {
                    limb = limb,
                    kind = kind,
                    wrench = wrench,
                    lo = 0f,
                    hi = hi,
                    cost = cap.cost / Mathf.Max(0.02f, e),
                    effectiveness = e,
                };
            }
        }
    }
}
