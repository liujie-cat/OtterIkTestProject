using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// What a closed skin holds below a level, measured on its posed mesh: each face clipped to its part below the
    /// level and summed as signed tetrahedra to a point on the level, where the cut face itself adds nothing. Heights
    /// are taken from the body's position, so the measure holds wherever the body has moved since the skin was posed.
    /// </summary>
    public sealed class SkinVolume
    {
        private readonly Transform body;
        private readonly SkinnedMeshRenderer skin;
        private readonly Mesh baked;
        private int[] triangles;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private Vector3[] points = new Vector3[0];
        private float sign = 1f;

        /// <summary>The skin's whole volume (m^3), as last posed.</summary>
        public float Total { get; private set; }
        /// <summary>The skin's lowest and highest points above the body's position (m), as last posed.</summary>
        public float Bottom { get; private set; }
        public float Top { get; private set; }

        public SkinVolume(Transform body, SkinnedMeshRenderer skin)
        {
            this.body = body;
            this.skin = skin;
            baked = new Mesh { name = "SkinVolume", hideFlags = HideFlags.DontSave };
            baked.MarkDynamic();
        }

        /// <summary>The body's skin: the enabled skinned mesh under it with the most vertices, or null.</summary>
        public static SkinnedMeshRenderer FindSkin(Transform body) =>
            body.GetComponentsInChildren<SkinnedMeshRenderer>()
                .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sharedMesh != null)
                .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();

        /// <summary>Takes the skin as it is posed now.</summary>
        public void Bake()
        {
            skin.BakeMesh(baked, true);
            // the model's own mesh need not be readable in Play; the baked copy is
            if (triangles == null) triangles = baked.triangles;
            baked.GetVertices(vertices);
            if (points.Length != vertices.Count) points = new Vector3[vertices.Count];
            // baked with the skin's scale, so placed by its position and turn alone
            var place = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
            Vector3 origin = body.position;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = place.MultiplyPoint3x4(vertices[i]) - origin;
                points[i] = p;
                bottom = Mathf.Min(bottom, p.y);
                top = Mathf.Max(top, p.y);
            }
            Bottom = bottom;
            Top = top;
            sign = 1f;
            float total = Below(top + 1f);
            // a skin wound inside out measures negative
            sign = total < 0f ? -1f : 1f;
            Total = Mathf.Abs(total);
        }

        /// <summary>The skin's volume below the level <paramref name="level"/> above the body's position (m^3).</summary>
        public float Below(float level)
        {
            if (level <= Bottom) return 0f;
            var o = new Vector3(0f, level, 0f);
            float sum = 0f;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = points[triangles[i]] - o, b = points[triangles[i + 1]] - o, c = points[triangles[i + 2]] - o;
                int under = (a.y < 0f ? 1 : 0) + (b.y < 0f ? 1 : 0) + (c.y < 0f ? 1 : 0);
                if (under == 0) continue;
                if (under == 3)
                {
                    sum += Tetra(a, b, c);
                    continue;
                }
                // turned round, winding kept, so that a is the corner on its own side of the level
                bool alone(Vector3 p) => under == 1 ? p.y < 0f : p.y >= 0f;
                if (alone(b)) (a, b, c) = (b, c, a);
                else if (alone(c)) (a, b, c) = (c, a, b);
                Vector3 ab = Cut(a, b), ca = Cut(a, c);
                sum += under == 1 ? Tetra(a, ab, ca) : Tetra(ab, b, c) + Tetra(ab, c, ca);
            }
            return sign * sum / 6f;
        }

        /// <summary>The level above the body's position with <paramref name="share"/> of the skin's volume below it.</summary>
        public float Level(float share)
        {
            float low = Bottom, high = Top, want = Mathf.Clamp01(share) * Total;
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (low + high);
                if (Below(mid) < want) low = mid;
                else high = mid;
            }
            return 0.5f * (low + high);
        }

        /// <summary>Frees the baked mesh.</summary>
        public void Release()
        {
            if (Application.isPlaying) Object.Destroy(baked);
            else Object.DestroyImmediate(baked);
        }

        private static float Tetra(Vector3 a, Vector3 b, Vector3 c) => Vector3.Dot(a, Vector3.Cross(b, c));

        private static Vector3 Cut(Vector3 a, Vector3 b) => Vector3.Lerp(a, b, a.y / (a.y - b.y));
    }
}
