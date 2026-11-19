using System.IO;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Pictures of the otter at chosen moments of a playback. The orthographic views show only its skinned meshes so
    /// the scene does not hide the limbs; the perspective ones look on from above and behind its left side, as from
    /// the Scene view, with WaterReference's grids and web trails and the controls' ring and guide arrow, so the body's
    /// motion can be set against the world, the strokes and where it is led. Single shots frame the whole body from
    /// above (head up), from its left and in perspective; strips are contact sheets over a kick (frames left to right,
    /// then the next row): close-ups of the hind feet from above, behind and the left, a perspective close-up riding
    /// with the hips, and a perspective view held still in the world over the whole strip, which the otter swims
    /// through. Written to Logs/Snapshots.
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

        readonly struct View
        {
            public readonly string name;
            public readonly Vector3 position;
            public readonly Quaternion rotation;
            /// <summary>Half the picture's height at the subject (m).</summary>
            public readonly float size;
            public readonly bool perspective;

            public View(string name, Vector3 position, Quaternion rotation, float size, bool perspective)
            {
                this.name = name;
                this.position = position;
                this.rotation = rotation;
                this.size = size;
                this.perspective = perspective;
            }
        }

        const int Layer = 31;
        const int CellWidth = 400, CellHeight = 320, Columns = 4;
        const float FieldOfView = 30f;

        static Texture2D[] sheets;
        static Vector3 stripCenterLocal;
        static View held;

        /// <summary>Moments to capture: segment index and seconds into it, matching OtterInputPlayback's scripts, in order and not overlapping.</summary>
        public static Shot[] For(string script) => script switch
        {
            "speeds" => new[]
            {
                new Shot(1, 0.1f, "1_park_kick_feet", 8, 0.06f),
                new Shot(3, 4.0f, "3_slow_feet", 8, 0.06f),
                new Shot(5, 4.0f, "5_cruise_feet", 8, 0.05f),
                new Shot(6, 0.5f, "6_sprint_kick_feet", 8, 0.04f),
                new Shot(6, 5.0f, "6_sprint_wave_feet", 8, 0.04f),
            },
            "turns" => new[]
            {
                new Shot(3, 0.15f, "03_park_large_right_feet", 8, 0.08f),
                new Shot(9, 0.3f, "09_slow_large_right_feet", 8, 0.1f),
                new Shot(15, 0.3f, "15_cruise_large_right_feet", 8, 0.1f),
                new Shot(23, 0.3f, "23_sprint_large_right_feet", 8, 0.08f),
            },
            _ => new[]
            {
                new Shot(1, 0.1f, "01_park_kick_feet", 8, 0.06f),
                new Shot(2, 0.3f, "02_park_click_turn_a"), new Shot(2, 0.6f, "02_park_click_turn_b"), new Shot(2, 0.9f, "02_park_click_turn_c"),
                new Shot(5, 1.5f, "05_slow_straight"),
                new Shot(5, 1.55f, "05_slow_straight_feet", 8, 0.1f),
                new Shot(6, 0.3f, "06_slow_turn_a"), new Shot(6, 0.6f, "06_slow_turn_b"),
                new Shot(8, 0.3f, "08_roll_prone_a"), new Shot(8, 0.6f, "08_roll_prone_b"), new Shot(8, 0.9f, "08_roll_prone_c"),
                new Shot(9, 1.5f, "09_cruise_straight"),
                new Shot(9, 1.55f, "09_cruise_straight_feet", 8, 0.08f),
                new Shot(10, 0.3f, "10_cruise_turn_a"), new Shot(10, 0.6f, "10_cruise_turn_b"),
                new Shot(11, 0.4f, "11_sprint_kick_feet", 8, 0.06f),
                new Shot(11, 2.4f, "11_sprint_wave_feet", 8, 0.08f),
                new Shot(11, 3.2f, "11_sprint_wave"),
                new Shot(15, 0.3f, "15_roll_supine_a"), new Shot(15, 0.6f, "15_roll_supine_b"),
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
            // the telemetry's hind ankle trails ride along under the hip
            var body = meshes.Cast<Renderer>().Concat(otter.GetComponentsInChildren<LineRenderer>().Where(l => l.enabled && l.positionCount > 1)).ToArray();
            var reference = Object.FindObjectsByType<WaterReference>(FindObjectsSortMode.None).SelectMany(w => w.Renderers).Where(r => r != null);
            var overlay = Object.FindObjectsByType<ControlsOverlay>(FindObjectsSortMode.None).SelectMany(o => o.Renderers).Where(r => r != null && r.enabled);
            var scene = body.Concat(reference).Concat(overlay).Distinct().ToArray();
            // from above and behind the left side, as the Scene view usually looks on
            Vector3 aside = (-right * 0.75f - forward * 0.45f + Vector3.up * 0.5f).normalized;
            string dir = Path.Combine("Logs", "Snapshots");
            Directory.CreateDirectory(dir);

            if (shot.frames <= 1)
            {
                var views = new[]
                {
                    Orthographic("top", bounds.center, Vector3.up, forward, extent * 0.8f, extent),
                    Orthographic("left", bounds.center, -right, Vector3.up, extent * 0.8f, extent),
                };
                Render(body, views, 1000, 800,
                    (v, tex) => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{views[v].name}.png"), tex.EncodeToPNG()));
                var persp = new[] { Perspective("persp", bounds.center, aside, extent * 0.9f) };
                Render(scene, persp, 1000, 800,
                    (v, tex) => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{persp[v].name}.png"), tex.EncodeToPNG()));
                return;
            }

            var hip = OtterRig.FindActiveBone(otter, OtterRig.Hip);
            if (hip == null) return;
            if (frame == 0)
            {
                var chains = OtterRig.FindLimbChains(otter);
                var hind = new[] { chains[(int)LimbId.HL], chains[(int)LimbId.HR] }.Where(c => c != null).ToArray();
                Vector3 feet = hind.Length > 0 ? hind.Aggregate(Vector3.zero, (s, c) => s + c.end.bone.position) / hind.Length : hip.position;
                stripCenterLocal = hip.InverseTransformPoint(Vector3.Lerp(hip.position, feet, 0.6f));
                // held still in the world, framing the otter's whole way through the strip from its left
                var rb = otter.GetComponent<Rigidbody>();
                Vector3 travel = rb != null ? Vector3.ProjectOnPlane(rb.velocity, Vector3.up) * (shot.interval * (shot.frames - 1)) : Vector3.zero;
                Vector3 side = (-right + Vector3.up * 0.6f).normalized;
                float half = (0.5f * travel.magnitude + extent * 0.9f) * CellHeight / CellWidth;
                held = Perspective("world", bounds.center + 0.5f * travel, side, Mathf.Max(extent * 0.7f, half));
                Release();
                int rows = (shot.frames + Columns - 1) / Columns;
                sheets = Enumerable.Range(0, 5).Select(_ => new Texture2D(CellWidth * Columns, CellHeight * rows, TextureFormat.RGB24, false)).ToArray();
            }
            if (sheets == null) return;
            Vector3 center = hip.TransformPoint(stripCenterLocal);
            var close = new[]
            {
                Orthographic("top", center, Vector3.up, forward, extent * 0.3f, extent),
                Orthographic("back", center, -forward, Vector3.up, extent * 0.3f, extent),
                Orthographic("left", center, -right, Vector3.up, extent * 0.3f, extent),
            };
            var wide = new[] { Perspective("persp", center, aside, extent * 0.45f), held };
            int column = frame % Columns, row = frame / Columns, rowCount = sheets[0].height / CellHeight;
            Render(body, close, CellWidth, CellHeight, (v, tex) => Place(v, tex));
            Render(scene, wide, CellWidth, CellHeight, (v, tex) => Place(close.Length + v, tex));
            if (frame < shot.frames - 1) return;
            var names = close.Concat(wide).Select(v => v.name).ToArray();
            for (int v = 0; v < names.Length; v++)
                File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{names[v]}.png"), sheets[v].EncodeToPNG());
            Release();

            void Place(int sheet, Texture2D tex) =>
                sheets[sheet].SetPixels(column * CellWidth, (rowCount - 1 - row) * CellHeight, CellWidth, CellHeight, tex.GetPixels());
        }

        /// <summary>Drops a strip that could not finish (its segment ended).</summary>
        public static void Release()
        {
            if (sheets != null)
                foreach (var s in sheets) Object.DestroyImmediate(s);
            sheets = null;
        }

        static View Orthographic(string name, Vector3 center, Vector3 from, Vector3 up, float size, float extent) =>
            new View(name, center + from * (extent + 1f), Quaternion.LookRotation(-from, up), size, false);

        static View Perspective(string name, Vector3 center, Vector3 from, float size) =>
            new View(name, center + from * (size / Mathf.Tan(0.5f * FieldOfView * Mathf.Deg2Rad)), Quaternion.LookRotation(-from, Vector3.up), size, true);

        static void Render(Renderer[] drawn, View[] views, int width, int height, System.Action<int, Texture2D> use)
        {
            var layers = drawn.Select(m => m.gameObject.layer).ToArray();
            foreach (var m in drawn) m.gameObject.layer = Layer;
            var go = new GameObject("LocomotionSnapshotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            cam.fieldOfView = FieldOfView;
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
                    cam.orthographic = !views[v].perspective;
                    cam.orthographicSize = views[v].size;
                    go.transform.SetPositionAndRotation(views[v].position, views[v].rotation);
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
                for (int i = 0; i < drawn.Length; i++) drawn[i].gameObject.layer = layers[i];
                cam.targetTexture = null;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }
    }
}
