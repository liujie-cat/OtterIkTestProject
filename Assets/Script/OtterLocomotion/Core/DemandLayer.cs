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
        [Tooltip("Yaw rate limit at yawRateRefSpeedBL (body lengths/s) and above (deg/s).")]
        public float cruiseYawRateMax = 180f;
        public float yawRateRefSpeedBL = 0.5f;

        [Header("Yaw rate -> yaw moment")]
        [Tooltip("M = I * gain * (r* - r) + angular drag feedforward (1/s).")]
        public float yawRateGain = 8f;

        [Header("Speed -> surge force")]
        [Tooltip("Speed (body lengths/s) the hind kicks hold at full stroke strength on the back, and on the belly (WebThrust). The stroke strength asked for is the speed target's share of it: kicks grow with the speed wanted, not with the push missing, since a web's push grows with the square of its speed through the water and a slow body gets far more from the same kick.")]
        public float supineKickSpeedBL = 0.95f;
        public float proneKickSpeedBL = 2.1f;
        [Tooltip("Kicks for u* + gain * (u* - u): how much harder or lighter the speed error makes them.")]
        public float speedGain = 1.5f;
        [Tooltip("How fast a lasting speed error trims the kicks while holding a steady speed on a straight course (1/s). A kick's push grows faster than its strength, beat and size together, so the kicks alone settle off the target at part strength.")]
        public float speedTrimRate = 2f;
        [Tooltip("Largest trim, as a share of the full-stroke speed, and the yaw rate target (deg/s) beyond which the trim holds still: turning costs speed of its own.")]
        public float speedTrimMax = 0.3f;
        public float speedTrimYawRate = 30f;
        [Tooltip("How fast the speed target may rise (body lengths/s^2).")]
        public float speedRiseRateBL = 2f;
        [Tooltip("How fast it falls (body lengths/s^2): the release glide and the ease-down to the supine cap. Slower than the water slows the body, so easing down glides rather than brakes.")]
        public float speedFallRateBL = 0.4f;

        [Header("Sideslip")]
        [Tooltip("Lateral force = m * (u r* - gain * sway): centripetal for the commanded turn plus sideslip damping (1/s).")]
        public float swayGain = 3f;

        [Header("Bank")]
        [Tooltip("Fraction of the coordinated-turn bank atan(u r* / g).")]
        [Range(0f, 1.5f)] public float bankCoordination = 0.8f;
        [Tooltip("Extra bank per deg/s of commanded yaw rate when turning in place, fading out by yawRateRefSpeedBL.")]
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
        /// <summary>Kick trim as a share of the full-stroke speed.</summary>
        private float trim;
        private int turnSign;
        private Vector3 heading = Vector3.forward;
        private bool hasHeading;

        public LocomotionDemand Current => demand;

        public void Reset(in BodyState body)
        {
            demand = default;
            speedTarget = Mathf.Max(0f, body.surge);
            trim = 0f;
            turnSign = 0;
            heading = body.forward;
            hasHeading = false;
        }

        /// <param name="speedCap">Posture's top speed (m/s). The settings' speeds are in body lengths per second (p.bodyLength).</param>
        /// <param name="speedRiseRate">Posture's limit on how fast the speed target rises (m/s^2), on top of the settings'.</param>
        /// <param name="kickSpeed">Posture's speed at full stroke strength (m/s).</param>
        /// <param name="fullStroke">Surge force (N) that asks the kicking limbs for full stroke strength.</param>
        public LocomotionDemand Step(DemandSettings s, in LocomotionIntent intent, in BodyState body, in BodyParameters p, float speedCap, float speedRiseRate, float kickSpeed, float fullStroke, float dt)
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
            float speed01 = Mathf.Clamp01(u / Mathf.Max(1e-3f, s.yawRateRefSpeedBL * p.bodyLength));
            float rLimit = Mathf.Lerp(s.pivotYawRateMax, s.cruiseYawRateMax, speed01);
            float rTarget = Mathf.Clamp((steering ? s.headingGain : s.releaseHeadingGain) * error, -rLimit, rLimit);

            // speed target: requested, capped by posture, slewed; never push forward while deliberately slowing
            float requested = steering ? Mathf.Min(intent.requestedSpeed, speedCap) : 0f;
            bool slowing = requested < speedTarget - 1e-4f;
            float rate = slowing ? s.speedFallRateBL * p.bodyLength : Mathf.Min(s.speedRiseRateBL * p.bodyLength, speedRiseRate);
            speedTarget = Mathf.MoveTowards(speedTarget, requested, rate * dt);
            // easing down, the target never waits above the body: one already slower kicks for the new target at once
            if (slowing) speedTarget = Mathf.Max(requested, Mathf.Min(speedTarget, Mathf.Max(0f, body.surge)));
            kickSpeed = Mathf.Max(1e-3f, kickSpeed);
            if (requested <= 0f) trim = 0f;
            else if (!slowing && speedTarget >= requested - 1e-4f && Mathf.Abs(rTarget) < s.speedTrimYawRate)
                trim = Mathf.Clamp(trim + s.speedTrimRate * (speedTarget - body.surge) / kickSpeed * dt, -s.speedTrimMax, s.speedTrimMax);
            float kick = speedTarget + s.speedGain * (speedTarget - body.surge) + trim * kickSpeed;
            float surgeForce = fullStroke * kick / kickSpeed;
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
