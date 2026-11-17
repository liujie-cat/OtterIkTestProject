using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// Collision shapes on the hind legs, measured from the skinned mesh: a capsule along each shin and over the ankle,
    /// and a box round each web and its toes, which turns and spreads with the web. The thighs have none: they sit in
    /// the pelvis against the tail's root, which they touch at rest as well. The shapes hang on the bones
    /// and are read where the bones are posed at the time, so a part posed after the legs (TailRudder) keeps itself out
    /// of them. Not PhysX colliders: under the otter's rigid body those would join its compound shape and change how it
    /// floats and turns, and the legs are posed by IK, not moved by contacts.
    /// </summary>
    [DisallowMultipleComponent]
    public class HindLegColliders : MonoBehaviour
    {
        [Tooltip("Scale on the thickness measured from the mesh.")]
        public float thickness = 1f;
        [Tooltip("Clearance kept round every shape (m).")]
        public float skin = 0.003f;
        public bool drawGizmos = true;

        private struct Capsule
        {
            public Transform from, to;
            public float radius;
        }

        private struct Box
        {
            public Transform bone;
            public Vector3 center;
            public Vector3[] halfAxes;
        }

        private readonly List<Capsule> capsules = new List<Capsule>();
        private readonly List<Box> boxes = new List<Box>();

        public bool Ready => capsules.Count + boxes.Count > 0;

        // after the IK components are enabled, at the rig's rest pose
        private void Start()
        {
            foreach (var ik in OtterRig.FindLimbChains(transform).Where((c, i) => c != null && !OtterRig.IsFore((LimbId)i)))
            {
                Transform knee = ik.mid.bone, ankle = ik.end.bone, web = OtterRig.Flipper(ankle);
                AddCapsule(knee, ankle);
                if (web != ankle)
                {
                    AddCapsule(ankle, web);
                    if (OtterRig.TryBox(web, out var center, out var halfAxes)) boxes.Add(new Box { bone = web, center = center, halfAxes = halfAxes });
                }
            }
            if (!Ready) Debug.LogWarning($"[HindLegColliders] No hind legs found under '{name}'.", this);
        }

        private void AddCapsule(Transform from, Transform to)
        {
            if (Vector3.Distance(from.position, to.position) < 1e-3f) return;
            if (!OtterRig.TryRadius(from, from.position, to.position, out float radius)) return;
            capsules.Add(new Capsule { from = from, to = to, radius = radius });
        }

        /// <summary>How the shapes were measured (cm), for the logs.</summary>
        public string Describe() =>
            $"{capsules.Count} capsules r {string.Join("/", capsules.Select(c => (c.radius * thickness * 100f).ToString("F1")))}, " +
            $"{boxes.Count} web boxes {string.Join("/", boxes.Select(b => string.Join("x", b.halfAxes.Select(a => (b.bone.TransformVector(a).magnitude * 200f).ToString("F1")))))}";

        /// <summary>
        /// How far a ball has to move to sit clear of every shape (zero when it is clear): out of the deepest overlap
        /// first, then out of whatever it still meets, a few times over. The webs are thin and give: once clear of the
        /// shins the ball moves out of them at most <paramref name="webStep"/>, so a web turning over fast against it
        /// only brushes it aside, while one pressed on it slowly pushes it clear.
        /// </summary>
        public Vector3 Push(Vector3 center, float radius, float webStep = float.PositiveInfinity)
        {
            Vector3 moved = Vector3.zero;
            bool firmWebs = float.IsPositiveInfinity(webStep);
            for (int pass = 0; pass < 3; pass++)
            {
                Vector3 push = Deepest(center + moved, radius + skin, true, firmWebs, out float depth);
                if (depth <= 0f) break;
                moved += push;
            }
            if (!firmWebs && webStep > 0f)
            {
                Vector3 push = Deepest(center + moved, radius + skin, false, true, out float depth);
                if (depth > 0f) moved += Vector3.ClampMagnitude(push, webStep);
            }
            return moved;
        }

        /// <summary>How deep a ball sits in the deepest shape it meets (m, 0 when clear; without the clearance).</summary>
        public float Depth(Vector3 center, float radius)
        {
            Deepest(center, radius, true, true, out float depth);
            return Mathf.Max(0f, depth);
        }

        // The way out of the shape the ball overlaps most, and how deep it is in it (negative = the gap to the nearest)
        private Vector3 Deepest(Vector3 center, float radius, bool shins, bool webs, out float depth)
        {
            depth = float.NegativeInfinity;
            Vector3 best = Vector3.zero;
            for (int i = 0; shins && i < capsules.Count; i++)
            {
                var c = capsules[i];
                Vector3 a = c.from.position, b = c.to.position, ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(center - a, ab) / Mathf.Max(1e-10f, ab.sqrMagnitude));
                Vector3 d = center - (a + ab * t);
                float dist = d.magnitude, overlap = radius + c.radius * thickness - dist;
                if (overlap <= depth) continue;
                depth = overlap;
                best = (dist > 1e-6f ? d / dist : Vector3.up) * overlap;
            }
            for (int i = 0; webs && i < boxes.Count; i++)
            {
                var box = boxes[i];
                Vector3 mid = box.bone.TransformPoint(box.center), rel = center - mid, closest = mid;
                float inside = float.PositiveInfinity;
                Vector3 exit = Vector3.zero;
                bool within = true;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 half = box.bone.TransformVector(box.halfAxes[k]) * (k == 0 ? thickness : 1f);
                    float h = half.magnitude;
                    if (h < 1e-6f) continue;
                    Vector3 axis = half / h;
                    float x = Vector3.Dot(rel, axis);
                    closest += axis * Mathf.Clamp(x, -h, h);
                    if (Mathf.Abs(x) > h) within = false;
                    else if (h - Mathf.Abs(x) < inside)
                    {
                        inside = h - Mathf.Abs(x);
                        exit = axis * (x >= 0f ? 1f : -1f);
                    }
                }
                Vector3 d = center - closest;
                float dist = d.magnitude;
                float overlap = within ? radius + inside : radius - dist;
                if (overlap <= depth) continue;
                depth = overlap;
                best = (within ? exit : dist > 1e-6f ? d / dist : Vector3.up) * overlap;
            }
            return best;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.8f);
            foreach (var c in capsules)
            {
                float r = c.radius * thickness;
                Vector3 a = c.from.position, b = c.to.position;
                Gizmos.DrawWireSphere(a, r);
                Gizmos.DrawWireSphere(b, r);
                Vector3 side = Vector3.Cross(b - a, Vector3.up).normalized * r, up = Vector3.Cross(side, b - a).normalized * r;
                Gizmos.DrawLine(a + side, b + side);
                Gizmos.DrawLine(a - side, b - side);
                Gizmos.DrawLine(a + up, b + up);
                Gizmos.DrawLine(a - up, b - up);
            }
            foreach (var box in boxes)
            {
                Vector3 mid = box.bone.TransformPoint(box.center);
                var h = box.halfAxes.Select((a, k) => box.bone.TransformVector(a) * (k == 0 ? thickness : 1f)).ToArray();
                for (int s = -1; s <= 1; s += 2)
                for (int t = -1; t <= 1; t += 2)
                {
                    Gizmos.DrawLine(mid + s * h[0] + t * h[1] - h[2], mid + s * h[0] + t * h[1] + h[2]);
                    Gizmos.DrawLine(mid + s * h[0] - h[1] + t * h[2], mid + s * h[0] + h[1] + t * h[2]);
                    Gizmos.DrawLine(mid - h[0] + s * h[1] + t * h[2], mid + h[0] + s * h[1] + t * h[2]);
                }
            }
        }
    }
}
