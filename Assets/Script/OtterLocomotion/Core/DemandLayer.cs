using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class DemandSettings
    {
        [Header("Heading -> yaw rate target")]
        [Tooltip("r* = gain * heading error (1/s), then limited.")]
        public float headingGain = 5f;
        [Tooltip("Gain used to finish the turn after the pointer is released (1/s).")]
        public float releaseHeadingGain = 2.5f;
        [Tooltip("Yaw rate limit when turning in place (deg/s).")]
        public float pivotYawRateMax = 150f;
        [Tooltip("Yaw rate limit at yawRateRefSpeed and above (deg/s).")]
        public float cruiseYawRateMax = 180f;
        public float yawRateRefSpeed = 2.5f;

        [Header("Yaw rate -> yaw moment")]
        [Tooltip("M = I * gain * (r* - r) + angular drag feedforward (1/s).")]
        public float yawRateGain = 8f;

        [Header("Speed -> surge force")]
        [Tooltip("F = m * gain * (u* - u) + drag feedforward (1/s).")]
        public float speedGain = 2.5f;
        [Tooltip("How fast the speed target may rise (m/s^2).")]
        public float speedRiseRate = 4f;
        [Tooltip("How fast it falls: the release glide and the ease-down to the supine cap (m/s^2).")]
        public float speedFallRate = 1.5f;

        [Header("Sideslip")]
        [Tooltip("Lateral force = m * (u r* - gain * sway): centripetal for the commanded turn plus sideslip damping (1/s).")]
        public float swayGain = 3f;

        [Header("Bank")]
        [Tooltip("Fraction of the coordinated-turn bank atan(u r* / g).")]
        [Range(0f, 1.5f)] public float bankCoordination = 0.8f;
        [Tooltip("Extra bank per deg/s of commanded yaw rate when turning in place, fading out by yawRateRefSpeed.")]
        public float pivotBankPerYawRate = 0.05f;
        public float bankMaxDeg = 25f;
    }

    /// <summary>
    /// Intent + measured state -> what the body needs: yaw rate, speed, and the surge/sway force and yaw moment that
    /// would produce them. Pure function of its inputs plus three bits of memory (slewed speed target, last turn
    /// direction, last requested heading).
    /// </summary>
    public sealed class DemandLayer
    {
        private LocomotionDemand demand;
        private float speedTarget;
        private int turnSign;
        private Vector3 heading = Vector3.forward;
        private bool hasHeading;

        public LocomotionDemand Current => demand;

        public void Reset(in BodyState body)
        {
            demand = default;
            speedTarget = Mathf.Max(0f, body.surge);
            turnSign = 0;
            heading = body.forward;
            hasHeading = false;
        }

        public LocomotionDemand Step(DemandSettings s, in LocomotionIntent intent, in BodyState body, in BodyParameters p, float speedCap, float dt)
        {
            const float g = 9.81f;
            bool steering = intent.Steering;
            if (steering)
            {
                heading = intent.heading;
                hasHeading = true;
            }

            // heading -> yaw rate target; near-reversals keep the direction already chosen instead of dithering
            float headingTarget = hasHeading ? Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg : body.headingDeg;
            float error = Mathf.DeltaAngle(body.headingDeg, headingTarget);
            if (Mathf.Abs(error) > 170f && turnSign != 0 && Mathf.Sign(error) != turnSign) error += 360f * turnSign;
            if (Mathf.Abs(error) > 1f) turnSign = error > 0f ? 1 : -1;
            float u = Mathf.Max(0f, body.surge);
            float speed01 = Mathf.Clamp01(u / Mathf.Max(1e-3f, s.yawRateRefSpeed));
            float rLimit = Mathf.Lerp(s.pivotYawRateMax, s.cruiseYawRateMax, speed01);
            float rTarget = Mathf.Clamp((steering ? s.headingGain : s.releaseHeadingGain) * error, -rLimit, rLimit);

            // speed target: requested, capped by posture, slewed; never push forward while deliberately slowing
            float requested = steering ? Mathf.Min(intent.requestedSpeed, speedCap) : 0f;
            bool slowing = requested < speedTarget - 1e-4f;
            speedTarget = Mathf.MoveTowards(speedTarget, requested, (slowing ? s.speedFallRate : s.speedRiseRate) * dt);
            float speed = body.Speed;
            float drag = (p.linearDrag + p.quadraticDrag * speed) * body.surge;
            float surgeForce = p.mass * s.speedGain * (speedTarget - body.surge) + drag;
            if (slowing) surgeForce = Mathf.Min(surgeForce, 0f);

            // yaw moment toward the yaw rate target, plus what the water's angular drag will take away
            float rateErrorRad = (rTarget - body.yawRate) * Mathf.Deg2Rad;
            float yawMoment = p.yawInertia * s.yawRateGain * rateErrorRad + p.angularLinearDrag * body.yawRate * Mathf.Deg2Rad;

            // lateral: centripetal force for the commanded turn, and damping of any sideslip
            float lateralAccel = u * rTarget * Mathf.Deg2Rad;
            float swayForce = p.mass * (lateralAccel - s.swayGain * body.sway);

            // bank into the turn: a share of the coordinated-turn angle, plus a little when pivoting in place
            float bank = s.bankCoordination * Mathf.Atan2(lateralAccel, g) * Mathf.Rad2Deg
                       + s.pivotBankPerYawRate * rTarget * (1f - speed01);

            demand = new LocomotionDemand
            {
                headingTargetDeg = body.headingDeg + error,
                headingErrorDeg = error,
                yawRateTarget = rTarget,
                yawRateLimit = rLimit,
                speedTarget = speedTarget,
                speedCap = speedCap,
                surgeForce = surgeForce,
                swayForce = swayForce,
                yawMoment = yawMoment,
                bankTargetDeg = Mathf.Clamp(bank, -s.bankMaxDeg, s.bankMaxDeg),
            };
            return demand;
        }
    }
}
