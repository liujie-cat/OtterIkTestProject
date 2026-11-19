using UnityEngine;

namespace OtterLocomotion
{
    // Conventions for the whole locomotion stack (planar body on the water surface):
    //   yaw / heading / yaw rate: degrees, + = clockwise seen from above (turning right), same as Unity's euler y
    //   body frame: x = forward (surge), y = right (sway)
    //   roll / bank: degrees about the forward axis, + = right side down

    public enum Posture { Prone, Supine }

    /// <summary>What the player asks for this physics step, from the gear the controls are in.</summary>
    public struct LocomotionIntent
    {
        public Gear gear;
        public Gait gait;
        public Posture posture;
        /// <summary>Direction to swim on XZ (unit).</summary>
        public Vector3 heading;
        /// <summary>Forward speed asked for (m/s).</summary>
        public float requestedSpeed;
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
        /// <summary>Speed the body is being brought to (after slew) and the speed asked for.</summary>
        public float speedTarget, requestedSpeed;
        public float surgeForce, swayForce, yawMoment;
        public float bankTargetDeg;

        // Reserved for leaving the surface plane; always 0 in the planar model.
        public float pitchTargetDeg, heaveForce;
    }

    /// <summary>Physical constants the demand layer needs to turn motion targets into forces.</summary>
    public struct BodyParameters
    {
        public float mass;
        /// <summary>Nose-to-rump length (m). Speeds in the settings are in body lengths per second, so they scale with the otter.</summary>
        public float bodyLength;
        public float yawInertia;
        /// <summary>Same model as WaterDrag: F = -(linear + quadratic*|v|) v, M = -angularLinear * w.</summary>
        public float linearDrag, quadraticDrag, angularLinearDrag;
    }
}
