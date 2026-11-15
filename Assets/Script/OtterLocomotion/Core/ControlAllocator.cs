using System;
using UnityEngine;

namespace OtterLocomotion
{
    [Serializable]
    public class AllocatorSettings
    {
        [Header("What matters when not everything can be met")]
        public float surgePriority = 1f;
        [Tooltip("0: the body's own sideways drag supplies the turning (centripetal) force; limbs push sideways only as a side effect of steering.")]
        public float swayPriority = 0f;
        public float yawPriority = 4f;
        [Tooltip("Error scales: this much body acceleration (m/s^2) or yaw acceleration (rad/s^2) counts as one unit.")]
        public float referenceAccel = 1f;
        public float referenceYawAccel = 10f;

        [Header("Effort")]
        [Tooltip("Effort against demand error. Small = meet the demand and use stroke costs only to share it out.")]
        public float effortWeight = 0.02f;
        [Tooltip("Penalty on changing a stroke between physics steps, against flicker.")]
        public float changeWeight = 0.05f;
        public int iterations = 40;
    }

    /// <summary>Per-limb outcome of an allocation, in the body frame (x forward, y right).</summary>
    public struct LimbForce
    {
        public Vector2 force;
        public float yawMoment;
        /// <summary>Some stroke of this limb is at its limit.</summary>
        public bool saturated;
    }

    /// <summary>
    /// Shares the demanded wrench (surge force, sway force, yaw moment) among the limbs' stroke primitives:
    /// box-constrained weighted least squares, minimise |W(Bu - v)|^2 + effort + change, lo &lt;= u &lt;= hi,
    /// solved by projected coordinate descent warm-started from the previous step.
    /// </summary>
    public sealed class ControlAllocator
    {
        private const int Rows = 3;
        private readonly float[] u;
        private readonly float[] g;
        private readonly float[,] h;

        public readonly LimbForce[] limbs = new LimbForce[OtterRig.LimbCount];
        public float[] StrokeForces => u;
        public Vector3 Demanded { get; private set; }
        public Vector3 Achieved { get; private set; }
        public int SaturatedStrokes { get; private set; }

        public ControlAllocator(int columns)
        {
            u = new float[columns];
            g = new float[columns];
            h = new float[columns, columns];
        }

        public void Reset() => Array.Clear(u, 0, u.Length);

        public void Solve(AllocatorSettings s, ActuatorModel model, in LocomotionDemand demand, in BodyParameters p)
        {
            var cols = model.columns;
            int n = cols.Length;
            Vector3 v = new Vector3(demand.surgeForce, demand.swayForce, demand.yawMoment);
            float fRef = Mathf.Max(1e-4f, p.mass * s.referenceAccel);
            float mRef = Mathf.Max(1e-6f, p.yawInertia * s.referenceYawAccel);
            Vector3 w = new Vector3(s.surgePriority / (fRef * fRef), s.swayPriority / (fRef * fRef), s.yawPriority / (mRef * mRef));
            float effort = s.effortWeight / (fRef * fRef);
            float change = s.changeWeight / (fRef * fRef);

            for (int i = 0; i < n; i++)
            {
                Vector3 bi = cols[i].wrench;
                for (int j = i; j < n; j++)
                {
                    Vector3 bj = cols[j].wrench;
                    float hij = bi.x * w.x * bj.x + bi.y * w.y * bj.y + bi.z * w.z * bj.z;
                    h[i, j] = hij;
                    h[j, i] = hij;
                }
                h[i, i] += effort * cols[i].cost + change;
                g[i] = bi.x * w.x * v.x + bi.y * w.y * v.y + bi.z * w.z * v.z + change * u[i];
                u[i] = Mathf.Clamp(u[i], cols[i].lo, cols[i].hi);
            }

            for (int it = 0; it < s.iterations; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (cols[i].hi <= cols[i].lo) { u[i] = 0f; continue; }
                    float r = g[i];
                    for (int j = 0; j < n; j++) r -= h[i, j] * u[j];
                    u[i] = Mathf.Clamp(u[i] + r / h[i, i], cols[i].lo, cols[i].hi);
                }
            }

            Vector3 achieved = Vector3.zero;
            int saturated = 0;
            Array.Clear(limbs, 0, limbs.Length);
            for (int i = 0; i < n; i++)
            {
                var c = cols[i];
                achieved += c.wrench * u[i];
                int l = (int)c.limb;
                limbs[l].force += new Vector2(c.wrench.x, c.wrench.y) * u[i];
                limbs[l].yawMoment += c.wrench.z * u[i];
                bool atLimit = c.hi > 1e-4f && (u[i] >= c.hi - 1e-3f * c.hi || (c.lo < 0f && u[i] <= c.lo + 1e-3f * c.hi));
                if (atLimit)
                {
                    saturated++;
                    limbs[l].saturated = true;
                }
            }
            Demanded = v;
            Achieved = achieved;
            SaturatedStrokes = saturated;
        }
    }
}
