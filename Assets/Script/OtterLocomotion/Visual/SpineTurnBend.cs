using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Bends the trunk along its path through a turn in the dolphin wave (LimbStrokeAnimator.Fluke), where the feet beat
    /// together and no push curls it (SpineKickSway): each spine joint turns out of the turn by the path's curvature,
    /// the yaw rate asked for over the speed, times how far back from the chest it lies, so the hips follow the chest
    /// round the bend rather than swinging out with it. An additive provider on the spine harmonizer, on top of its
    /// turn bend; adds itself to the harmonizer's providers for this Play.
    /// </summary>
    [DefaultExecutionOrder(315)]
    [DisallowMultipleComponent]
    public class SpineTurnBend : ExpSpinePoseProviderBase
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("Found on this object when empty.")]
        public LimbStrokeAnimator animator;
        [Tooltip("Found in the scene when empty.")]
        public ExpShadowSpineHarmonizer harmonizer;
        [Tooltip("How hard the trunk bends, as a multiple of lying along its path: the harmonizer's chain softens it, and a supple body bends round further than its path.")]
        public float bend = 2.5f;
        [Tooltip("Most the hips turn from the chest's line (deg); the joints between them keep to the same arc.")]
        public float maxBend = 30f;
        [Tooltip("Slowest speed the path's curvature is taken at (m/s): slower, a turn on the spot would curl the body up.")]
        public float minSpeed = 0.4f;
        [Tooltip("How quickly the bend follows the turn (s).")]
        public float bendTime = 0.12f;

        // each spine joint's distance back from the chest (m), hips first
        private float[] back = new float[0];
        private bool added;

        /// <summary>The path's curvature now (1/m, + = curving to the right), dolphin wave or not.</summary>
        public float Curvature { get; private set; }

        public override bool Additive => true;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (animator == null) animator = GetComponent<LimbStrokeAnimator>();
            if (harmonizer == null)
                harmonizer = FindObjectsByType<ExpShadowSpineHarmonizer>(FindObjectsSortMode.None).FirstOrDefault(h => h.isActiveAndEnabled);
        }

        private void Start()
        {
            if (harmonizer == null) return;
            if (!harmonizer.providers.Contains(this))
            {
                harmonizer.providers.Add(this);
                added = true;
            }
        }

        private void OnDestroy()
        {
            if (added && harmonizer != null) harmonizer.providers.Remove(this);
        }

        // before the harmonizer reads the providers in its LateUpdate
        private void Update()
        {
            var nodes = harmonizer != null && harmonizer.decoupler != null ? harmonizer.decoupler.shadowNodes : null;
            int n = nodes != null ? nodes.Count : 0;
            if (back.Length != n)
            {
                back = new float[n];
                for (int i = n - 2; i >= 0; i--)
                    back[i] = back[i + 1] + (nodes[i] != null && nodes[i + 1] != null ? Vector3.Distance(nodes[i].position, nodes[i + 1].position) : 0f);
            }
            float goal = 0f;
            if (brain != null && brain.isActiveAndEnabled)
                goal = brain.Demand.yawRateTarget * Mathf.Deg2Rad / Mathf.Max(minSpeed, brain.State.surge);
            Curvature = Mathf.Lerp(Curvature, goal, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-3f, bendTime)));
        }

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            float fluke = animator != null && animator.isActiveAndEnabled ? animator.Fluke : 0f;
            if (index < 0 || index >= back.Length || back[0] <= 1e-4f || fluke <= 1e-3f) return false;
            // curving to the right, the joints behind the chest point left of its line
            float hips = Mathf.Clamp(-bend * Curvature * back[0] * Mathf.Rad2Deg, -maxBend, maxBend);
            float angle = fluke * hips * back[index] / back[0];
            if (Mathf.Abs(angle) < 0.01f) return false;
            targetLocalRot = Quaternion.Euler(0f, angle, 0f) * bindLocalRot;
            weight = globalWeight;
            return true;
        }
    }
}
