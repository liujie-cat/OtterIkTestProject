using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// A long, slender body slips through the water nose first far more easily than sideways. WaterDrag resists every
    /// direction alike, so when MovementControllerRB swings the body round its old travel carries on sideways and the
    /// otter drifts and stalls through every turn; this adds the body's extra resistance across its length, so it
    /// carves round behind its heading as a real otter does.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public class BodyKeel : MonoBehaviour
    {
        [Tooltip("Extra drag on sideways travel over the drag along the body, per unit of the body's mass (1/s): sideways drift dies down in about 1/this seconds.")]
        public float sidewaysDamping = 12f;

        private Rigidbody body;

        private void Awake() => body = GetComponent<Rigidbody>();

        private void FixedUpdate()
        {
            if (body == null) return;
            Vector3 side = Vector3.ProjectOnPlane(body.rotation * Vector3.right, Vector3.up).normalized;
            float slip = Vector3.Dot(body.velocity, side);
            body.AddForce(-side * (slip * sidewaysDamping * body.mass), ForceMode.Force);
        }
    }
}
