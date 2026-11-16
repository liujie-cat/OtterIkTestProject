using System;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>Calls back in LateUpdate after the limb IK solve (AdvancedTwoBoneIK, order 360), to pose bones below the solved chains.</summary>
    [DefaultExecutionOrder(370)]
    [AddComponentMenu("")]
    public class AfterLimbIK : MonoBehaviour
    {
        public event Action Solved;

        private void LateUpdate() => Solved?.Invoke();
    }
}
