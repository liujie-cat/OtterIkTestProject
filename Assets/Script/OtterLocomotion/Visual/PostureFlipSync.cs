using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Makes the spine's roll follow the brain: its flip the posture, so the limb the allocation picks is the one on that
    /// side in the picture, and its tilt the bank into the turn. The tilt turns the spine about its own length on top of
    /// the flip, so the side toward the turn sinks on the belly and on the back alike; it is strongest at the chest and
    /// carried on down to the hips, and the limbs, set on the chest and hip bones, roll with them.
    /// </summary>
    [DefaultExecutionOrder(160)]
    [DisallowMultipleComponent]
    public class PostureFlipSync : MonoBehaviour
    {
        [Tooltip("Found on this object when empty.")]
        public OtterLocomotionBrain brain;
        [Tooltip("Found in the scene when empty.")]
        public ExpSpineRollFlipProvider rollProvider;

        private void Awake()
        {
            if (brain == null) brain = GetComponent<OtterLocomotionBrain>();
            if (rollProvider == null)
                rollProvider = FindObjectsByType<ExpSpineRollFlipProvider>(FindObjectsSortMode.None).FirstOrDefault(p => p.isActiveAndEnabled);
        }

        private void Update()
        {
            if (brain == null || rollProvider == null) return;
            rollProvider.isBackstroke = brain.Posture.Current == Posture.Supine;
            // the tilt is + = left side down, the bank + = right side down
            rollProvider.additiveRoll = -brain.Demand.bankTargetDeg;
        }
    }
}
