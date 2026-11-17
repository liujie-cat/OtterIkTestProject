using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Makes the body move when the limbs push: MovementControllerRB's thrust is delivered through the power strokes of
    /// OtterLocomotionBrain's rhythm instead of its own kick timer, so the otter surges as the feet kick and the paws
    /// stroke and coasts between, and it swings round mostly while the strokes that turn it push. The average thrust
    /// is unchanged (each stroke's share is weighted by its forward force), so the speeds stay as tuned. Nothing pushes
    /// before a limb does. With WebThrust on, the webs push the body through the water instead and the movement
    /// controller is asked for no thrust; the turning still follows the strokes.
    /// </summary>
    [DefaultExecutionOrder(180)]
    [DisallowMultipleComponent]
    public class StrokePropulsion : MonoBehaviour, MovementControllerRB.IPropulsionTiming
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        public MovementControllerRB movement;
        public WebThrust webs;
        [Tooltip("How sharply each push peaks in the middle of its power stroke (2 = a rounded surge; higher = a shorter, harder shove).")]
        [Range(1f, 6f)] public float sharpness = 2f;
        [Tooltip("How much of the body's turning waits for the strokes that turn it (0 = it turns steadily, 1 = only while they push).")]
        [Range(0f, 1f)] public float turnPulse = 0.1f;

        public float Thrust { get; private set; }
        public float Turn { get; private set; } = 1f;

        private float shapeScale;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (movement == null) movement = GetComponent<MovementControllerRB>();
            if (webs == null) webs = GetComponent<WebThrust>();
        }

        private void OnEnable()
        {
            if (movement != null) movement.PropulsionTiming = this;
        }

        private void OnDisable()
        {
            if (movement != null && ReferenceEquals(movement.PropulsionTiming, this)) movement.PropulsionTiming = null;
        }

        // After the brain has stepped the rhythm, before the movement controller pushes the body
        private void FixedUpdate()
        {
            if (brain == null || !brain.HasLimbGeometry)
            {
                Thrust = 1f;
                Turn = 1f;
                return;
            }
            shapeScale = 1f / Integral(sharpness);
            var rhythm = brain.Rhythm.limbs;
            var allocation = brain.Allocation.limbs;
            float push = 0f, pushWeight = 0f, turn = 0f, turnWeight = 0f;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var st = rhythm[i];
                if (!st.active || st.parking || st.drive <= 0f) continue;
                float pulse = Pulse(st);
                // a stroke's share of the thrust follows its forward force, of the turning its yaw moment
                float forward = Mathf.Max(0f, allocation[i].force.x) * st.drive;
                float yaw = Mathf.Abs(allocation[i].yawMoment) * st.drive;
                push += forward * pulse;
                pushWeight += forward;
                turn += yaw * pulse;
                turnWeight += yaw;
            }
            Thrust = webs != null && webs.isActiveAndEnabled && webs.HasWebs ? 0f : pushWeight > 1e-4f ? push / pushWeight : 0f;
            Turn = turnWeight > 1e-5f ? Mathf.Lerp(1f, turn / turnWeight, turnPulse) : 1f;
        }

        // A limb's push this step over its average over the cycle: a bump through the power stroke, nothing after it
        private float Pulse(in LimbStroke st)
        {
            if (!st.InPower) return 0f;
            float u = st.phase / Mathf.Max(1e-3f, st.powerFraction);
            return Mathf.Pow(Mathf.Sin(Mathf.PI * u), sharpness) * shapeScale / Mathf.Max(1e-3f, st.powerFraction);
        }

        // integral of sin(pi u)^k over 0..1
        private static float Integral(float k)
        {
            const int n = 64;
            float sum = 0f;
            for (int j = 0; j < n; j++) sum += Mathf.Pow(Mathf.Sin(Mathf.PI * (j + 0.5f) / n), k);
            return Mathf.Max(1e-4f, sum / n);
        }
    }
}
