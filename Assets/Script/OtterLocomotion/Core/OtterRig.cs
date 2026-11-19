using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    public enum LimbId { FL, FR, HL, HR }

    /// <summary>Naming conventions of the sea otter rig (built on the cat skeleton) that the locomotion code relies on.</summary>
    public static class OtterRig
    {
        public const int LimbCount = 4;
        public const string Hip = "root.x", Chest = "spine_03.x", Head = "head.x", TailRoot = "c_tail_00.x";
        /// <summary>The neck's joints from the chest out, the head last; each the child of the one before.</summary>
        public static readonly string[] Neck = { "c_subneck_1.x", "neck.x", Head };

        public static bool IsFore(LimbId limb) => limb == LimbId.FL || limb == LimbId.FR;

        /// <summary>-1 for the anatomical left limbs, +1 for the right.</summary>
        public static int Side(LimbId limb) => limb == LimbId.FL || limb == LimbId.HL ? -1 : 1;

        /// <summary>Forelimb chains are the "_dupli_001" copies of the cat's leg bones; sides end in .l / .r.</summary>
        public static bool TryClassify(AdvancedTwoBoneIK ik, out LimbId limb)
        {
            limb = default;
            if (ik == null || ik.root == null || ik.root.bone == null) return false;
            string bone = ik.root.bone.name;
            bool right = bone.EndsWith(".r");
            if (!right && !bone.EndsWith(".l")) return false;
            limb = bone.Contains("dupli")
                ? (right ? LimbId.FR : LimbId.FL)
                : (right ? LimbId.HR : LimbId.HL);
            return true;
        }

        public static Transform FindActiveBone(Transform root, string name) =>
            root.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == name && t.gameObject.activeInHierarchy);

        /// <summary>The tail's bones, TailRoot first, each the "c_tail_" child of the one before.</summary>
        public static List<Transform> TailBones(Transform root)
        {
            var bones = new List<Transform>();
            for (var bone = FindActiveBone(root, TailRoot); bone != null && bones.Count < 12;)
            {
                bones.Add(bone);
                Transform next = null;
                foreach (Transform child in bone)
                    if (child.name.StartsWith("c_tail_") && child.gameObject.activeInHierarchy) { next = child; break; }
                bone = next;
            }
            return bones;
        }

        /// <summary>The spine bone a limb hangs from (chest for forelimbs, hip for hind limbs); limb motion is measured in its space.</summary>
        public static Transform Girdle(Transform limbRoot)
        {
            for (var p = limbRoot.parent; p != null; p = p.parent)
                if (p.name == Chest || p.name == Hip) return p;
            return limbRoot.parent;
        }

        /// <summary>The flipper hanging from a hind chain's end bone: its child with bones below it (foot, not the twist helper).</summary>
        public static Transform Flipper(Transform end)
        {
            foreach (Transform child in end)
                if (child.childCount > 0 && child.gameObject.activeInHierarchy) return child;
            return end;
        }

        /// <summary>
        /// The skinned mesh's vertices that follow a bone (it and the bones below it carry at least half their weight),
        /// in the world, as posed now; null when no skin has that many on the bone. The mesh need not be readable: the
        /// skin is baked twice with the bone turned a little in between, and the share of the rigid motion each vertex
        /// follows is its weight.
        /// </summary>
        public static List<Vector3> FollowingVertices(Transform bone, int minimum = 8)
        {
            foreach (var skin in bone.root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skin.sharedMesh == null || skin.bones == null || !skin.bones.Any(b => b != null && b.IsChildOf(bone))) continue;
                var before = new Mesh();
                var after = new Mesh();
                var points = new List<Vector3>();
                var saved = bone.localRotation;
                try
                {
                    skin.BakeMesh(before, true);
                    var turn = Quaternion.AngleAxis(10f, bone.right);
                    bone.rotation = turn * bone.rotation;
                    skin.BakeMesh(after, true);
                    bone.localRotation = saved;
                    var a = before.vertices;
                    var b = after.vertices;
                    Vector3 pivot = bone.position;
                    for (int v = 0; v < a.Length && v < b.Length; v++)
                    {
                        Vector3 p = skin.transform.TransformPoint(a[v]);
                        float rigid = (turn * (p - pivot) - (p - pivot)).magnitude;
                        if (rigid < 1e-3f) continue;
                        if ((skin.transform.TransformPoint(b[v]) - p).magnitude / rigid >= 0.5f) points.Add(p);
                    }
                }
                finally
                {
                    bone.localRotation = saved;
                    Object.DestroyImmediate(before);
                    Object.DestroyImmediate(after);
                }
                if (points.Count >= minimum) return points;
            }
            return null;
        }

        /// <summary>
        /// The flat of a foot, from the skinned mesh: the vertices that follow the bone, in the bone's own space.
        /// normal = the thinnest direction (the way the web faces), length = the longest, center = the middle of the
        /// web; extents = the spread along normal / middle / length (metres, one sigma).
        /// </summary>
        public static bool TryFootPlane(Transform foot, out Vector3 normal, out Vector3 length, out Vector3 center, out Vector3 extents)
        {
            normal = length = center = extents = Vector3.zero;
            var world = FollowingVertices(foot);
            if (world == null) return false;
            var points = world.Select(foot.InverseTransformPoint).ToList();
            Vector3 mean = points.Aggregate(Vector3.zero, (s, p) => s + p) / points.Count;
            Principal(points, mean, out var values, out var axes);
            normal = axes[0];
            length = axes[2];
            center = mean;
            float scale = foot.lossyScale.x;
            extents = new Vector3(Mathf.Sqrt(Mathf.Max(0f, values[0])), Mathf.Sqrt(Mathf.Max(0f, values[1])), Mathf.Sqrt(Mathf.Max(0f, values[2]))) * scale;
            return true;
        }

        /// <summary>
        /// A box around the vertices that follow a bone, in the bone's own space, so it turns and scales with the bone:
        /// its middle, and its half-axes from the thinnest to the longest (each along a principal direction of the
        /// vertices, as long as the box reaches that way).
        /// </summary>
        public static bool TryBox(Transform bone, out Vector3 center, out Vector3[] halfAxes)
        {
            center = Vector3.zero;
            halfAxes = null;
            var world = FollowingVertices(bone);
            if (world == null) return false;
            var points = world.Select(bone.InverseTransformPoint).ToList();
            Vector3 mean = points.Aggregate(Vector3.zero, (s, p) => s + p) / points.Count;
            Principal(points, mean, out _, out var axes);
            halfAxes = new Vector3[3];
            center = mean;
            for (int k = 0; k < 3; k++)
            {
                float lo = points.Min(p => Vector3.Dot(p - mean, axes[k])), hi = points.Max(p => Vector3.Dot(p - mean, axes[k]));
                center += axes[k] * (0.5f * (lo + hi));
                halfAxes[k] = axes[k] * (0.5f * (hi - lo));
            }
            return true;
        }

        /// <summary>
        /// The thickness about a bone's line from <paramref name="from"/> to <paramref name="to"/> (world): the given
        /// quantile of how far the vertices that follow the bone and lie alongside that stretch sit from it (metres).
        /// </summary>
        public static bool TryRadius(Transform bone, Vector3 from, Vector3 to, out float radius, float quantile = 0.5f)
        {
            radius = 0f;
            var world = FollowingVertices(bone);
            Vector3 axis = to - from;
            if (world == null || axis.sqrMagnitude < 1e-10f) return false;
            var distances = new List<float>();
            foreach (var p in world)
            {
                float t = Vector3.Dot(p - from, axis) / axis.sqrMagnitude;
                if (t >= 0f && t <= 1f) distances.Add(Vector3.Distance(p, from + axis * t));
            }
            if (distances.Count < 4) return false;
            distances.Sort();
            radius = distances[Mathf.Clamp(Mathf.RoundToInt(quantile * (distances.Count - 1)), 0, distances.Count - 1)];
            return true;
        }

        /// <summary>
        /// The half-width and half-height about a bone's line from <paramref name="from"/> to <paramref name="to"/>
        /// (world), height along <paramref name="up"/>: the given quantile of how far the vertices that follow the bone
        /// and lie alongside that stretch sit from it each way (metres).
        /// </summary>
        public static bool TryHalfExtents(Transform bone, Vector3 from, Vector3 to, Vector3 up, out float halfWidth, out float halfHeight, float quantile = 0.85f)
        {
            halfWidth = halfHeight = 0f;
            var world = FollowingVertices(bone);
            Vector3 axis = to - from;
            Vector3 side = Vector3.Cross(up, axis);
            if (world == null || axis.sqrMagnitude < 1e-10f || side.sqrMagnitude < 1e-10f) return false;
            side.Normalize();
            Vector3 height = Vector3.Cross(axis, side).normalized;
            var across = new List<float>();
            var heights = new List<float>();
            foreach (var p in world)
            {
                float t = Vector3.Dot(p - from, axis) / axis.sqrMagnitude;
                if (t < 0f || t > 1f) continue;
                Vector3 d = p - (from + axis * t);
                across.Add(Mathf.Abs(Vector3.Dot(d, side)));
                heights.Add(Mathf.Abs(Vector3.Dot(d, height)));
            }
            if (across.Count < 4) return false;
            across.Sort();
            heights.Sort();
            int i = Mathf.Clamp(Mathf.RoundToInt(quantile * (across.Count - 1)), 0, across.Count - 1);
            halfWidth = across[i];
            halfHeight = heights[i];
            return true;
        }


        // The principal directions of a point cloud about its mean, from the least spread to the most, with their variances.
        private static void Principal(List<Vector3> points, Vector3 mean, out float[] values, out Vector3[] axes)
        {
            var c = new float[3, 3];
            foreach (var p in points)
            {
                Vector3 d = p - mean;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        c[i, j] += d[i] * d[j] / points.Count;
            }
            SymmetricEigen(c, out var raw, out var vectors);
            var order = Enumerable.Range(0, 3).OrderBy(i => raw[i]).ToArray();
            values = order.Select(i => raw[i]).ToArray();
            axes = order.Select(i => vectors[i].normalized).ToArray();
        }

        // Jacobi rotations on a symmetric 3x3 matrix; eigenvectors as columns of the accumulated rotation.
        public static void SymmetricEigen(float[,] a, out float[] values, out Vector3[] vectors)
        {
            var v = new float[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 32; sweep++)
            {
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Mathf.Abs(a[p, q]) < 1e-12f) continue;
                        float theta = 0.5f * Mathf.Atan2(2f * a[p, q], a[q, q] - a[p, p]);
                        float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
                        for (int k = 0; k < 3; k++)
                        {
                            float akp = a[k, p], akq = a[k, q];
                            a[k, p] = cs * akp - sn * akq;
                            a[k, q] = sn * akp + cs * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            float apk = a[p, k], aqk = a[q, k];
                            a[p, k] = cs * apk - sn * aqk;
                            a[q, k] = sn * apk + cs * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            float vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = cs * vkp - sn * vkq;
                            v[k, q] = sn * vkp + cs * vkq;
                        }
                    }
                }
            }
            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
            vectors = new[] { new Vector3(v[0, 0], v[1, 0], v[2, 0]), new Vector3(v[0, 1], v[1, 1], v[2, 1]), new Vector3(v[0, 2], v[1, 2], v[2, 2]) };
        }

        /// <summary>The otter's limb IK chains under a body, by limb; only active chains with all three bones set.</summary>
        public static AdvancedTwoBoneIK[] FindLimbChains(Transform body)
        {
            var chains = new AdvancedTwoBoneIK[LimbCount];
            foreach (var ik in Object.FindObjectsByType<AdvancedTwoBoneIK>(FindObjectsSortMode.None))
            {
                if (!ik.isActiveAndEnabled || ik.root?.bone == null || !ik.root.bone.IsChildOf(body)) continue;
                if (ik.mid == null || ik.mid.bone == null || ik.end == null || ik.end.bone == null) continue;
                if (TryClassify(ik, out var limb)) chains[(int)limb] = ik;
            }
            return chains;
        }
    }
}
