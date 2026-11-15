using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Reads the rigidbody into a BodyState. Yaw rate comes from the heading difference rather than
    /// angularVelocity, so it is right whether the body is turned by torque or by MoveRotation.
    /// </summary>
    public sealed class BodyStateEstimator
    {
        public float accelHalfLife = 0.08f;

        private BodyState state;
        private bool initialized;

        public BodyState State => state;

        public void Reset(Rigidbody rb)
        {
            initialized = false;
            Step(rb, 0f);
        }

        public BodyState Step(Rigidbody rb, float dt)
        {
            Vector3 fwd = rb.rotation * Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-8f ? fwd.normalized : Vector3.forward;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            float raw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float heading = initialized ? state.headingDeg + Mathf.DeltaAngle(state.headingDeg, raw) : raw;

            Vector3 v = rb.velocity;
            v.y = 0f;
            float surge = Vector3.Dot(v, fwd), sway = Vector3.Dot(v, right);
            float yawRate = initialized && dt > 0f ? (heading - state.headingDeg) / dt : 0f;

            if (initialized && dt > 0f)
            {
                float a = 1f - Mathf.Pow(0.5f, dt / Mathf.Max(1e-4f, accelHalfLife));
                state.surgeAccel = Mathf.Lerp(state.surgeAccel, (surge - state.surge) / dt, a);
                state.yawAccel = Mathf.Lerp(state.yawAccel, (yawRate - state.yawRate) / dt, a);
            }
            else
            {
                state.surgeAccel = state.yawAccel = 0f;
            }

            state.position = rb.position;
            state.forward = fwd;
            state.right = right;
            state.headingDeg = heading;
            state.yawRate = yawRate;
            state.surge = surge;
            state.sway = sway;
            initialized = true;
            return state;
        }
    }
}
