using System.IO;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Orthographic pictures of the otter at chosen moments of a playback, showing only its skinned meshes so the water
    /// and the scene do not hide the limbs. Single shots frame the whole body from above (head up) and from its left;
    /// strips are close-up contact sheets of the hind feet over a kick (frames left to right, then the next row),
    /// from above, behind and the left. Written to Logs/Snapshots.
    /// </summary>
    static class LocomotionSnapshots
    {
        public readonly struct Shot
        {
            public readonly int segment;
            public readonly float time;
            public readonly string name;
            /// <summary>1 = a whole-body picture; more = a hind-foot strip, one frame every interval seconds.</summary>
            public readonly int frames;
            public readonly float interval;

            public Shot(int segment, float time, string name, int frames = 1, float interval = 0f)
            {
                this.segment = segment;
                this.time = time;
                this.name = name;
                this.frames = frames;
                this.interval = interval;
            }
        }

        const int Layer = 31;
        const int CellWidth = 400, CellHeight = 320, Columns = 4;

        static Texture2D[] sheets;
        static Vector3 stripCenterLocal;

        /// <summary>Moments to capture: segment index and seconds into it, matching OtterInputPlayback's scripts, in order and not overlapping.</summary>
        public static Shot[] For(string script) => script switch
        {
            "posture" => new Shot[0],
            "turns" => new[]
            {
                new Shot(2, 0.15f, "02_ring_small_right_feet", 8, 0.06f),
                new Shot(3, 0.15f, "03_ring_large_right_feet", 8, 0.08f),
                new Shot(6, 1.5f, "06_swim_straight_feet", 8, 0.1f),
                new Shot(7, 0.2f, "07_swim_small_left_feet", 8, 0.1f),
                new Shot(9, 0.3f, "09_swim_large_right_feet", 8, 0.1f),
                new Shot(15, 1.6f, "15_sprint_straight_feet", 8, 0.08f),
                new Shot(18, 0.3f, "18_sprint_large_right_feet", 8, 0.08f),
            },
            _ => new[]
            {
                new Shot(1, 1.5f, "1_swim_straight"),
                new Shot(1, 1.55f, "1_swim_straight_feet", 8, 0.1f),
                new Shot(1, 1.28f, "1_swim_snap_feet", 8, 0.025f),
                new Shot(2, 0.30f, "2_swim_turn_a"), new Shot(2, 0.42f, "2_swim_turn_b"), new Shot(2, 0.54f, "2_swim_turn_c"),
                new Shot(4, 1.6f, "4_sprint_straight_feet", 8, 0.08f),
                new Shot(5, 0.30f, "5_sprint_turn_a"), new Shot(5, 0.45f, "5_sprint_turn_b"),
                new Shot(7, 0.15f, "7_pivot_a"), new Shot(7, 0.30f, "7_pivot_b"), new Shot(7, 0.45f, "7_pivot_c"),
                new Shot(7, 0.5f, "7_pivot_feet", 8, 0.06f),
            },
        };

        /// <summary>Takes a single shot, or frame number <paramref name="frame"/> of a strip (written after its last frame).</summary>
        public static void Take(Transform otter, string label, Shot shot, int frame)
        {
            var meshes = otter.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            if (meshes.Length == 0) return;
            var bounds = meshes[0].bounds;
            foreach (var m in meshes) bounds.Encapsulate(m.bounds);
            float extent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            Vector3 forward = otter.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            string dir = Path.Combine("Logs", "Snapshots");
            Directory.CreateDirectory(dir);

            if (shot.frames <= 1)
            {
                var views = new (string view, Vector3 from, Vector3 up)[]
                {
                    ("top", Vector3.up, forward),
                    ("left", -right, Vector3.up),
                };
                Render(meshes, bounds.center, extent * 0.8f, extent, 1000, 800, views,
                    (v, tex) => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{views[v].view}.png"), tex.EncodeToPNG()));
                return;
            }

            var hip = OtterRig.FindActiveBone(otter, OtterRig.Hip);
            if (hip == null) return;
            var strip = new (string view, Vector3 from, Vector3 up)[]
            {
                ("top", Vector3.up, forward),
                ("back", -forward, Vector3.up),
                ("left", -right, Vector3.up),
            };
            if (frame == 0)
            {
                var chains = OtterRig.FindLimbChains(otter);
                var hind = new[] { chains[(int)LimbId.HL], chains[(int)LimbId.HR] }.Where(c => c != null).ToArray();
                Vector3 feet = hind.Length > 0 ? hind.Aggregate(Vector3.zero, (s, c) => s + c.end.bone.position) / hind.Length : hip.position;
                stripCenterLocal = hip.InverseTransformPoint(Vector3.Lerp(hip.position, feet, 0.6f));
                Release();
                int rows = (shot.frames + Columns - 1) / Columns;
                sheets = strip.Select(_ => new Texture2D(CellWidth * Columns, CellHeight * rows, TextureFormat.RGB24, false)).ToArray();
            }
            if (sheets == null) return;
            int column = frame % Columns, row = frame / Columns, rowCount = sheets[0].height / CellHeight;
            Render(meshes, hip.TransformPoint(stripCenterLocal), extent * 0.3f, extent, CellWidth, CellHeight, strip,
                (v, tex) => sheets[v].SetPixels(column * CellWidth, (rowCount - 1 - row) * CellHeight, CellWidth, CellHeight, tex.GetPixels()));
            if (frame < shot.frames - 1) return;
            for (int v = 0; v < strip.Length; v++)
                File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{strip[v].view}.png"), sheets[v].EncodeToPNG());
            Release();
        }

        /// <summary>Drops a strip that could not finish (its segment ended).</summary>
        public static void Release()
        {
            if (sheets != null)
                foreach (var s in sheets) Object.DestroyImmediate(s);
            sheets = null;
        }

        static void Render(SkinnedMeshRenderer[] meshes, Vector3 center, float size, float extent, int width, int height,
            (string view, Vector3 from, Vector3 up)[] views, System.Action<int, Texture2D> use)
        {
            var layers = meshes.Select(m => m.gameObject.layer).ToArray();
            foreach (var m in meshes) m.gameObject.layer = Layer;
            var go = new GameObject("LocomotionSnapshotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = size;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = extent * 2f + 2f;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.64f);
            var rt = new RenderTexture(width, height, 24);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            cam.targetTexture = rt;
            try
            {
                for (int v = 0; v < views.Length; v++)
                {
                    go.transform.SetPositionAndRotation(center + views[v].from * (extent + 1f), Quaternion.LookRotation(-views[v].from, views[v].up));
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    use(v, tex);
                }
            }
            finally
            {
                for (int i = 0; i < meshes.Length; i++) meshes[i].gameObject.layer = layers[i];
                cam.targetTexture = null;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }
    }
}
