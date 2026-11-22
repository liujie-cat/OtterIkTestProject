using System;
using System.Collections.Generic;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class DiveSettings
    {
        [Tooltip("How deep a dive levels off (m below where the body floats), unless the dive key is held on.")]
        public float depth = 0.8f;
        [Tooltip("The duck dive: the way lunges up over a crest this high (m) at crestDeg, then plunges at plungeDeg (deg below level), so the hips and hind feet come up over the crest out of the water as they follow the chest under.")]
        public float crest = 0.06f;
        public float crestDeg = 35f;
        public float plungeDeg = 70f;
        [Tooltip("How steeply it swims down while the dive key is held (deg below level), and up to the surface (deg above).")]
        public float descendDeg = 40f;
        public float riseDeg = 50f;
        [Tooltip("The tightest the way bends (m radius): the chest arcs through it and the body after it.")]
        public float bendRadius = 0.15f;
        [Tooltip("How far above the sea floor it levels off (m).")]
        public float floorClearance = 0.3f;
        [Tooltip("How hard both hind feet kick as the hips go over the crest (0..1), the chest this far (m, seen from above) past its top.")]
        [Range(0f, 1f)] public float plungeKick = 1f;
        public float kickPast = 0.1f;
        [Tooltip("Time (s) for the body to go over onto the way, and off it to float again back at the surface.")]
        public float blendTime = 0.15f;
    }

    /// <summary>
    /// The way the otter swims through the depth, as the dive key steers it: how far above or below where it floats the
    /// way runs (m) at each place it has swum, seen from above, and on ahead along its slope. Sent under, once the body
    /// is over on its belly, the way lunges up over a low crest and plunges steeply (a duck dive), the hips and hind feet
    /// coming up over the crest out of the water as they follow the chest under, both feet kicking as they do; it levels
    /// off at depth. Held deeper it slopes on down, levelling off where let go or above the floor; sent back up it
    /// slopes up and levels off at the surface, where the body goes back to floating. It bends no tighter than
    /// bendRadius. Each part of the body passes along the same way in turn (DolphinTrack).
    /// </summary>
    public sealed class DivePath
    {
        private enum Leg { Afloat, Crest, Plunge, Level, Descend, Rise }

        /// <summary>How far the body has gone over to swimming along the way (0..1).</summary>
        public float Weight { get; private set; }
        /// <summary>On the way through the depth: going under, under, or coming back up.</summary>
        public bool Under => leg != Leg.Afloat;
        /// <summary>Going under, from rolling onto the belly to the bottom of the plunge, or coming back up.</summary>
        public bool DivingOrSurfacing => leg == Leg.Crest || leg == Leg.Plunge || leg == Leg.Rise || rolling;
        /// <summary>How deep the way runs where the chest is now (m below where the body floats).</summary>
        public float Depth => -height;
        /// <summary>How fast the way takes the chest up (m/s; down is negative), last step.</summary>
        public float ClimbRate { get; private set; }

        // where the chest has swum to, seen from above (m), the way's height there and its pitch (rad, up positive);
        // and the way behind it, a knot every KnotSpacing (oldest first)
        private Leg leg;
        private float travelled, height, pitch;
        private readonly List<Vector2> knots = new List<Vector2>();
        private const float KnotSpacing = 0.01f;
        private const int Knots = 300;
        private const float SubStep = 0.005f;
        private float crestTop;
        private bool rolling, kicked;

        public void Reset()
        {
            leg = Leg.Afloat;
            travelled = height = pitch = 0f;
            Weight = ClimbRate = 0f;
            rolling = false;
            knots.Clear();
        }

        /// <summary>How far above where the body floats the way runs (m) a distance ahead of the chest, seen from above
        /// (m; behind it negative): behind, where the chest went; ahead, on along its slope now.</summary>
        public float Height(float along)
        {
            if (along >= 0f) return height + Mathf.Tan(pitch) * along;
            float at = travelled + along;
            Vector2 next = new Vector2(travelled, height);
            for (int j = knots.Count - 1; j >= 0; j--)
            {
                Vector2 k = knots[j];
                if (k.x <= at) return next.x - k.x > 1e-6f ? Mathf.Lerp(k.y, next.y, (at - k.x) / (next.x - k.x)) : k.y;
                next = k;
            }
            return next.y;
        }

        /// <summary>The way's slope (rise over run) a distance ahead of the chest, seen from above (m).</summary>
        public float Slope(float along) => (Height(along + 0.01f) - Height(along - 0.01f)) / 0.02f;

        /// <summary>Which way the body swims on at a place along it (m ahead of the chest, seen from above): its heading
        /// (level, unit) tilted up or down the way's slope there, as far as the body is on the way.</summary>
        public Vector3 Tangent(Vector3 heading, float along) =>
            Weight > 0f ? Vector3.Slerp(heading, (heading + Vector3.up * Slope(along)).normalized, Weight) : heading;

        /// <param name="diving">Sent under (GearMachine.Diving).</param>
        /// <param name="deeper">The dive key held: on down.</param>
        /// <param name="moved">How far the body has swum since the last step, seen from above (m).</param>
        /// <param name="supine">How far the body is over on its back (0..1): the dive waits for it to be on its belly.</param>
        /// <param name="floor">How deep the sea floor is below where the body floats (m).</param>
        /// <returns>The plunge's kick is due: the hips are going over the crest.</returns>
        public bool Step(DiveSettings d, bool diving, bool deeper, float moved, float supine, float floor, float dt)
        {
            float before = height;
            bool kick = false;
            float bend = Mathf.Max(0.01f, d.bendRadius);
            float lowest = -Mathf.Max(0f, floor - d.floorClearance);
            float dive = Mathf.Max(lowest, -d.depth);
            rolling = leg == Leg.Afloat && diving;
            if (leg == Leg.Afloat && diving && supine < 0.25f)
            {
                leg = Leg.Crest;
                kicked = false;
                crestTop = float.PositiveInfinity;
                rolling = false;
            }
            else if (leg == Leg.Rise && diving) leg = Leg.Plunge;
            else if (leg != Leg.Afloat && leg != Leg.Rise && !diving) leg = Leg.Rise;

            int steps = Mathf.Max(1, Mathf.CeilToInt(moved / SubStep));
            float ds = moved / steps;
            for (int k = 0; k < steps; k++)
            {
                travelled += ds;
                if (leg == Leg.Afloat)
                {
                    height = pitch = 0f;
                    Record();
                    continue;
                }
                // the height the way takes to bend level from its pitch now
                float levelling = bend * (1f - Mathf.Cos(pitch));
                if (leg == Leg.Crest && height >= d.crest - levelling) leg = Leg.Plunge;
                if (leg == Leg.Plunge && height <= dive + levelling) leg = deeper ? Leg.Descend : Leg.Level;
                if (leg == Leg.Level && deeper && height > lowest + bend * (1f - Mathf.Cos(d.descendDeg * Mathf.Deg2Rad)) + 0.02f) leg = Leg.Descend;
                if (leg == Leg.Descend && (!deeper || height <= lowest + levelling)) leg = Leg.Level;
                float aim = leg switch
                {
                    Leg.Crest => d.crestDeg,
                    Leg.Plunge => -d.plungeDeg,
                    Leg.Descend => -d.descendDeg,
                    Leg.Rise => -height <= levelling ? 0f : d.riseDeg,
                    _ => 0f,
                } * Mathf.Deg2Rad;
                float was = pitch;
                pitch = Mathf.MoveTowards(pitch, aim, ds / (bend * Mathf.Max(0.05f, Mathf.Cos(pitch))));
                height += Mathf.Tan(0.5f * (was + pitch)) * ds;
                if (leg == Leg.Plunge && was > 0f && pitch <= 0f) crestTop = travelled;
                if (!kicked && travelled >= crestTop + d.kickPast)
                {
                    kicked = true;
                    kick = true;
                }
                if (leg == Leg.Rise && height >= 0f)
                {
                    height = pitch = 0f;
                    leg = Leg.Afloat;
                }
                Record();
            }
            Weight = Mathf.MoveTowards(Weight, Under ? 1f : 0f, dt / Mathf.Max(1e-3f, d.blendTime));
            ClimbRate = dt > 0f ? (height - before) / dt : 0f;
            return kick;
        }

        private void Record()
        {
            if (knots.Count > 0 && travelled - knots[knots.Count - 1].x < KnotSpacing) return;
            knots.Add(new Vector2(travelled, height));
            if (knots.Count > Knots) knots.RemoveAt(0);
        }
    }
}
