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
        /// The flat of a foot, from the skinned mesh: the vertices that follow the bone (it and the bones below it carry
        /// at least half their weight), in the bone's own space. normal = the thinnest direction (the way the web
        /// faces), length = the longest, center = the middle of the web; extents = the spread along normal / middle /
        /// length (metres, one sigma). The mesh need not be readable: the skin is baked twice with the bone turned a
        /// little in between, and the share of the rigid motion each vertex follows is its weight.
        /// </summary>
        public static bool TryFootPlane(Transform foot, out Vector3 normal, out Vector3 length, out Vector3 center, out Vector3 extents)
        {
            normal = length = center = extents = Vector3.zero;
            foreach (var skin in foot.root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skin.sharedMesh == null || skin.bones == null || !skin.bones.Any(b => b != null && b.IsChildOf(foot))) continue;
                var before = new Mesh();
                var after = new Mesh();
                var points = new System.Collections.Generic.List<Vector3>();
                var saved = foot.localRotation;
                try
                {
                    skin.BakeMesh(before, true);
                    var turn = Quaternion.AngleAxis(10f, foot.right);
                    foot.rotation = turn * foot.rotation;
                    skin.BakeMesh(after, true);
                    foot.localRotation = saved;
                    var a = before.vertices;
                    var b = after.vertices;
                    Vector3 pivot = foot.position;
                    for (int v = 0; v < a.Length && v < b.Length; v++)
                    {
                        Vector3 p = skin.transform.TransformPoint(a[v]);
                        float rigid = (turn * (p - pivot) - (p - pivot)).magnitude;
                        if (rigid < 1e-3f) continue;
                        if ((skin.transform.TransformPoint(b[v]) - p).magnitude / rigid >= 0.5f) points.Add(foot.InverseTransformPoint(p));
                    }
                }
                finally
                {
                    foot.localRotation = saved;
                    Object.DestroyImmediate(before);
                    Object.DestroyImmediate(after);
                }
                if (points.Count < 8) continue;
                Vector3 mean = points.Aggregate(Vector3.zero, (s, p) => s + p) / points.Count;
                var c = new float[3, 3];
                foreach (var p in points)
                {
                    Vector3 d = p - mean;
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            c[i, j] += d[i] * d[j] / points.Count;
                }
                SymmetricEigen(c, out var values, out var vectors);
                var order = Enumerable.Range(0, 3).OrderBy(i => values[i]).ToArray();
                normal = vectors[order[0]].normalized;
                length = vectors[order[2]].normalized;
                center = mean;
                float scale = foot.lossyScale.x;
                extents = new Vector3(Mathf.Sqrt(Mathf.Max(0f, values[order[0]])), Mathf.Sqrt(Mathf.Max(0f, values[order[1]])), Mathf.Sqrt(Mathf.Max(0f, values[order[2]]))) * scale;
                return true;
            }
            return false;
        }

        // Jacobi rotations on a symmetric 3x3 matrix; eigenvectors as columns of the accumulated rotation.
        private static void SymmetricEigen(float[,] a, out float[] values, out Vector3[] vectors)
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
