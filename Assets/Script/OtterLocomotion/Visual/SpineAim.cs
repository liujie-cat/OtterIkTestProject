using OtterIK.Neo.Experiment;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Turns the spine toward the target as ExpShadowAimProvider does, the chest the most and the hips the least, worked
    /// out in the body's own frame (rootReference), which the harmonizer turns the shadows in. The base works it out in
    /// the world, which matches only facing +Z: facing anywhere else its pitch toward the target comes out partly as a
    /// roll (all of it facing along X) or upside down, and the spine's bow from each kick (SpineKickWave) changes with
    /// where the otter faces.
    /// </summary>
    public class SpineAim : ExpShadowAimProvider
    {
        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;
            if (decoupler == null || target == null || rootReference == null) return false;
            Transform shadow = jointOrShadow;
            if (shadow == null)
            {
                if (index < 0 || index >= decoupler.shadowNodes.Count) return false;
                shadow = decoupler.shadowNodes[index];
            }
            Vector3 toward = rootReference.InverseTransformDirection(target.position - shadow.position);
            if (toward.sqrMagnitude < 1e-10f) return false;
            toward.Normalize();

            // past suppressionStartAngle the target is behind: the spine gives up turning to it rather than flip side to side
            float angle = Vector3.Angle(Vector3.forward, toward);
            float safety = angle > suppressionStartAngle && suppressionStartAngle < 180f
                ? Mathf.Clamp01(1f - (angle - suppressionStartAngle) / (180f - suppressionStartAngle)) : 1f;
            if (angle > maxAngle)
            {
                Vector3 axis = Vector3.Cross(Vector3.forward, toward);
                toward = Quaternion.AngleAxis(maxAngle, axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up) * Vector3.forward;
            }

            int total = decoupler.shadowNodes.Count;
            float along = total > 1 ? (float)index / (total - 1) : 0f;
            targetLocalRot = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.forward, toward), Mathf.Lerp(0.3f, 1f, along)) * bindLocalRot;
            weight = weightCurve.Evaluate(along) * safety;
            return true;
        }
    }
}
