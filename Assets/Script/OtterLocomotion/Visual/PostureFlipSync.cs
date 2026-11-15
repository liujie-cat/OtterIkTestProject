using System.Linq;
using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Makes the spine's flip follow the brain's posture, so the limb the allocation picks is the one on that side in
    /// the picture. Overrides the flip toggled elsewhere (SpineFlipTestController's F key); the tilt keys still work.
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
        }
    }
}
