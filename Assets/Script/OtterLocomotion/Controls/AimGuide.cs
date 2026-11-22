using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Moves the point the spine aims at (the aim provider's target): picked up, onto the guide point the head looks
    /// at; let go, back over releaseTime to a point ahead along the heading. It is kept level with the body's way
    /// through the depth and follows smoothly. Runs before the spine's pass reads it.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    [DisallowMultipleComponent]
    public class AimGuide : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterControls controls;
        [Tooltip("Found below this object when empty; the point moved is its target.")]
        public ExpShadowAimProvider aim;
        [Tooltip("Height the point is kept at (m) at the surface; under water it goes down with the body's way through the depth (DivePath).")]
        public float planeY = 0f;
        [Tooltip("Let go, how far ahead along the heading the point rests (m).")]
        public float idleAhead = 1.2f;
        [Tooltip("Time to blend from the guide point to the point ahead once let go (s).")]
        public float releaseTime = 0.35f;
        [Tooltip("How quickly the point follows (1/s).")]
        public float followSpeed = 22f;

        // kept here, not read back from the target: under the body it would be carried along between frames
        private Vector3 point, lastHeld;
        private float releasedAt = float.NegativeInfinity;
        private bool held, placed;

        private void Awake()
        {
            if (controls == null) controls = GetComponent<OtterControls>();
            if (aim == null) aim = GetComponentInChildren<ExpShadowAimProvider>();
        }

        private void LateUpdate()
        {
            if (controls == null || aim == null || aim.target == null) return;
            var gears = controls.Gears;
            Vector3 goal;
            if (gears.Selected)
            {
                goal = lastHeld = gears.AimPoint;
                held = true;
            }
            else
            {
                if (held)
                {
                    held = false;
                    releasedAt = Time.time;
                }
                Vector3 ahead = Vector3.ProjectOnPlane(gears.Heading, Vector3.up);
                if (ahead.sqrMagnitude < 1e-6f) ahead = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                goal = transform.position + ahead.normalized * idleAhead;
                float t = releaseTime > 0f ? Mathf.Clamp01((Time.time - releasedAt) / releaseTime) : 1f;
                goal = Vector3.Lerp(lastHeld, goal, t);
            }
            goal.y = planeY + controls.DivePath.Height(0f);
            if (!placed)
            {
                point = aim.target.position;
                placed = true;
            }
            point = Vector3.Lerp(point, goal, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            aim.target.position = point;
        }
    }
}
