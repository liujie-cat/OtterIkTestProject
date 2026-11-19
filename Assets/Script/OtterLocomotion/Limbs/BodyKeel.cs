using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// A long, slender body slips through the water nose first far more easily than sideways. WaterDrag resists every
    /// direction alike, so when OtterControls swings the body round its old travel carries on sideways and the
    /// otter drifts and stalls through every turn; this adds the body's extra resistance across its length, so it
    /// carves round behind its heading as a real otter does. The resistance centres ahead of the middle, as for any
    /// swimmer: the body turns about a point toward its head, head first, its hind end swinging round behind.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public class BodyKeel : MonoBehaviour
    {
        [Tooltip("Extra drag on sideways travel over the drag along the body, per unit of the body's mass (1/s): sideways drift dies down in about 1/this seconds.")]
        public float sidewaysDamping = 12f;
        [Tooltip("Where the sideways resistance centres, from the body's middle (0) to its head (1): the point it turns about.")]
        [Range(0f, 1f)] public float pivot = 0.5f;

        private Rigidbody body;
        private Transform head;

        private void Awake() => body = GetComponent<Rigidbody>();

        private void Start() => head = OtterRig.FindActiveBone(transform, OtterRig.Head);

        private void FixedUpdate()
        {
            if (body == null) return;
            Vector3 side = Vector3.ProjectOnPlane(body.rotation * Vector3.right, Vector3.up).normalized;
            Vector3 centre = body.worldCenterOfMass;
            Vector3 at = head != null ? Vector3.Lerp(centre, head.position, pivot) : centre;
            at.y = centre.y;
            float slip = Vector3.Dot(body.GetPointVelocity(at), side);
            body.AddForce(-side * (slip * sidewaysDamping * body.mass), ForceMode.Force);
        }
    }
}
