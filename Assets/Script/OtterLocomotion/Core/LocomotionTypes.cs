using UnityEngine;

namespace OtterLocomotion
{
    // Conventions for the whole locomotion stack (planar body on the water surface):
    //   yaw / heading / yaw rate: degrees, + = clockwise seen from above (turning right), same as Unity's euler y
    //   body frame: x = forward (surge), y = right (sway)
    //   roll / bank: degrees about the forward axis, + = right side down

    public enum SpeedBand { Rest, Aim, Swim, Sprint }

    public enum Posture { Prone, Supine }

    /// <summary>What the player asks for this physics step.</summary>
    public struct LocomotionIntent
    {
        public SpeedBand band;
        /// <summary>Desired direction on XZ (unit). Meaningful while band != Rest.</summary>
        public Vector3 heading;
        /// <summary>Requested forward speed before posture caps (m/s).</summary>
        public float requestedSpeed;

        public bool Steering => band != SpeedBand.Rest;
    }

    /// <summary>Measured rigid-body state on the surface plane.</summary>
    public struct BodyState
    {
        public Vector3 position;
        public Vector3 forward, right;
        /// <summary>Unwrapped, so it stays continuous through full turns.</summary>
        public float headingDeg;
        public float yawRate;
        public float surge, sway;
        /// <summary>Low-pass filtered derivatives (m/s^2, deg/s^2).</summary>
        public float surgeAccel, yawAccel;

        // Reserved for leaving the surface plane (diving, porpoising); always 0 in the planar model.
        public float pitchDeg, heaveSpeed;

        public float Speed => Mathf.Sqrt(surge * surge + sway * sway);
    }

    /// <summary>
    /// What the body needs from its limbs this step, in the body frame. Forces in N, moment in N·m.
    /// The allocator turns this into per-limb strokes; nothing here knows about limbs.
    /// </summary>
    public struct LocomotionDemand
    {
        public float headingTargetDeg, headingErrorDeg;
        public float yawRateTarget, yawRateLimit;
        /// <summary>Speed the body is being brought to (after posture cap and slew) and the cap itself.</summary>
        public float speedTarget, speedCap;
        public float surgeForce, swayForce, yawMoment;
        public float bankTargetDeg;

        // Reserved for leaving the surface plane; always 0 in the planar model.
        public float pitchTargetDeg, heaveForce;
    }

    /// <summary>Physical constants the demand layer needs to turn motion targets into forces.</summary>
    public struct BodyParameters
    {
        public float mass;
        public float yawInertia;
        /// <summary>Same model as WaterDrag: F = -(linear + quadratic*|v|) v, M = -angularLinear * w.</summary>
        public float linearDrag, quadraticDrag, angularLinearDrag;
    }
}
