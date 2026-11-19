using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Paces the body's turning by the limbs' strokes: OtterControls swings the body round mostly while the strokes that
    /// turn it push, and less between them. The webs (WebThrust) push the body along.
    /// </summary>
    [DefaultExecutionOrder(180)]
    [DisallowMultipleComponent]
    public class StrokePropulsion : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("How sharply each push peaks in the middle of its power stroke (2 = a rounded surge; higher = a shorter, harder shove).")]
        [Range(1f, 6f)] public float sharpness = 2f;
        [Tooltip("How much of the body's turning waits for the strokes that turn it (0 = it turns steadily, 1 = only while they push).")]
        [Range(0f, 1f)] public float turnPulse = 0.1f;

        /// <summary>This physics step's share of the usual turning response: high while the turning strokes push, low between them.</summary>
        public float Turn { get; private set; } = 1f;

        private float shapeScale;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
        }

        // After the brain has stepped the rhythm, before OtterControls turns the body
        private void FixedUpdate()
        {
            if (brain == null || !brain.HasLimbGeometry)
            {
                Turn = 1f;
                return;
            }
            shapeScale = 1f / Integral(sharpness);
            var rhythm = brain.Rhythm.limbs;
            var allocation = brain.Allocation.limbs;
            float turn = 0f, turnWeight = 0f;
            for (int i = 0; i < OtterRig.LimbCount; i++)
            {
                var st = rhythm[i];
                if (!st.active || st.parking || st.drive <= 0f) continue;
                // a stroke's share of the turning follows its yaw moment
                float yaw = Mathf.Abs(allocation[i].yawMoment) * st.drive;
                turn += yaw * Pulse(st);
                turnWeight += yaw;
            }
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
