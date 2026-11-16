using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// How the hind pushes bend the trunk side to side. Each push bows the body out on the pushing foot's side, so it
    /// curls away from that foot: hips swung toward it, tail tip and chest away. The way and the size follow the yaw
    /// moment the allocation gives the push, so the feet take turns curling the body left and right swimming straight,
    /// while in a turn the outside foot's hard push curls it into the turn and the light inside one hardly at all.
    /// Works from where the feet are in the world, so it holds on the back as well as prone.
    /// </summary>
    public static class HindBend
    {
        /// <summary>-1..1, + = curled to the right (the right side hollow); a bump over each power stroke.</summary>
        /// <param name="fullMoment">Yaw moment of a push (N·m) that bends the body most of the way.</param>
        /// <param name="lead">Read the strokes this far ahead (s), for a follower that lags by about as much.</param>
        public static float Now(OtterLocomotionBrain brain, float fullMoment, float lead = 0f)
        {
            if (brain == null || !brain.HasLimbGeometry) return 0f;
            float ahead = Time.time - Time.fixedTime + lead;
            float bend = Part(brain, LimbId.HL, ahead, fullMoment) + Part(brain, LimbId.HR, ahead, fullMoment);
            return Mathf.Clamp(bend, -1f, 1f);
        }

        // a push that turns the body left (- moment) bows it out on the right, so it curls left
        private static float Part(OtterLocomotionBrain brain, LimbId limb, float ahead, float fullMoment)
        {
            float push = Push(brain.Rhythm.limbs[(int)limb], ahead);
            return push > 0f ? push * Tanh(brain.Allocation.limbs[(int)limb].yawMoment / Mathf.Max(1e-4f, fullMoment)) : 0f;
        }

        // 0..1 over a power stroke, 0 outside it
        private static float Push(in LimbStroke st, float ahead)
        {
            if (!st.active || st.parking || st.drive <= 0f) return 0f;
            float phase = Mathf.Repeat(st.phase + st.rate * ahead, 1f);
            if (phase >= st.powerFraction) return 0f;
            return Mathf.Sin(Mathf.PI * phase / Mathf.Max(1e-3f, st.powerFraction));
        }

        private static float Tanh(float x)
        {
            float e = Mathf.Exp(-2f * Mathf.Abs(x));
            return Mathf.Sign(x) * (1f - e) / (1f + e);
        }
    }
}
