using System.IO;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Pictures of the otter at chosen moments of a playback. The orthographic views show only its skinned meshes so
    /// the scene does not hide the limbs, those from the side through a pane of the sea from the surface down, so the
    /// waterline shows on the body; the perspective ones look on from above and behind its left side, as from
    /// the Scene view, with the sea, WaterReference's grids and web trails and the controls' ring and guide arrow, so
    /// the body's motion can be set against the water, the strokes and where it is led. Single shots frame the whole
    /// body from above (head up), from its left, from behind (how it leans into a turn) and in perspective, and add the game view: the whole scene through the
    /// camera the controls read the pointer through, as the player sees it. Strips are contact sheets over a kick
    /// (frames left to right, then the next row): close-ups of the hind feet from above, behind and the left, a
    /// perspective close-up riding with the hips, and a perspective view held still in the world over the whole strip,
    /// which the otter swims through. Forepaw strips ride with the chest instead, seen from above, from below, where
    /// the paws work under the chest, and from the side outside the turn, where the paddling paw is (rolling over, the
    /// side it rolls down onto, where the paw taking the roll's stroke is), and a slice through the shoulders and
    /// forepaws seen from ahead along the body, where a roll's sweep round the shoulder shows; their close-up perspective
    /// from that side a little ahead and below, all without the sea. Body strips ride with the chest too, the whole
    /// body from its left against the waterline, for the wave along it. Written to Logs/Snapshots.
    /// </summary>
    static class LocomotionSnapshots
    {
        public readonly struct Shot
        {
            public readonly int segment;
            public readonly float time;
            public readonly string name;
            /// <summary>1 = a whole-body picture; more = a strip of close-ups, one frame every interval seconds.</summary>
            public readonly int frames;
            public readonly float interval;
            /// <summary>The strip frames the forepaws under the chest rather than the hind feet.</summary>
            public readonly bool paws;
            /// <summary>The strip frames the whole body from its left rather than the hind feet.</summary>
            public readonly bool body;

            public Shot(int segment, float time, string name, int frames = 1, float interval = 0f, bool paws = false, bool body = false)
            {
                this.segment = segment;
                this.time = time;
                this.name = name;
                this.frames = frames;
                this.interval = interval;
                this.paws = paws;
                this.body = body;
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
            /// <summary>Looking through a pane of the sea from the surface down (a level view).</summary>
            public readonly bool waterline;
            /// <summary>How far from the camera it draws (m), 0 for all the way.</summary>
            public readonly float depth;

            public View(string name, Vector3 position, Quaternion rotation, float size, bool perspective, bool waterline = false, float depth = 0f)
            {
                this.name = name;
                this.position = position;
                this.rotation = rotation;
                this.size = size;
                this.perspective = perspective;
                this.waterline = waterline;
                this.depth = depth;
            }
        }

        const int Layer = 31;
        const int CellWidth = 400, CellHeight = 320, Columns = 4;
        const float FieldOfView = 30f;

        static Texture2D[] sheets;
        static Vector3 stripCenterLocal;
        /// <summary>A forepaw strip's side view: from the right (+1) or the left (-1), outside the turn.</summary>
        static float outsideSign;
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
            "circles" => new[]
            {
                new Shot(3, 1.70f, "03_slow_circle_push", 8, 1f / 60f),
                new Shot(3, 4.0f, "03_slow_circle_feet", 8, 0.06f), new Shot(3, 5.0f, "03_slow_circle"),
                new Shot(6, 1.5f, "06_cruise_circle_paws", 8, 0.19f, paws: true),
                new Shot(6, 4.0f, "06_cruise_circle_feet", 8, 0.06f), new Shot(6, 5.0f, "06_cruise_circle"),
                new Shot(7, 1.2f, "07_cruise_tight_paws", 8, 0.12f, paws: true),
                new Shot(7, 2.5f, "07_cruise_tight_feet", 8, 0.06f), new Shot(7, 3.5f, "07_cruise_tight"),
                new Shot(9, 2.5f, "09_sprint_circle_feet", 8, 0.05f),
            },
            "dolphin" => new[]
            {
                new Shot(4, 2.0f, "04_wave_body", 8, 0.09f, body: true),
                new Shot(4, 3.0f, "04_wave_feet", 8, 0.06f), new Shot(4, 3.7f, "04_wave"),
                new Shot(5, 2.5f, "05_sweep_feet", 8, 0.08f),
                new Shot(6, 0.6f, "06_large_turn_feet", 8, 0.12f),
                new Shot(6, 1.8f, "06_large_turn_paws", 8, 0.12f, paws: true),
                new Shot(7, 1.6f, "07_wave_again_body", 8, 0.09f, body: true),
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
                new Shot(5, 1.5f, "05_slow_straight"), new Shot(5, 1.85f, "05_slow_push"),
                new Shot(5, 1.55f, "05_slow_straight_feet", 8, 0.1f),
                new Shot(6, 0.3f, "06_slow_turn_a"), new Shot(6, 0.6f, "06_slow_turn_b"),
                new Shot(8, 0.11f, "08_roll_prone_paws", 8, 0.05f, paws: true), new Shot(8, 0.9f, "08_roll_prone_c"),
                new Shot(9, 1.5f, "09_cruise_straight"),
                new Shot(9, 1.55f, "09_cruise_straight_feet", 8, 0.08f),
                new Shot(10, 0.3f, "10_cruise_turn_a"), new Shot(10, 0.6f, "10_cruise_turn_b"),
                new Shot(11, 0.4f, "11_sprint_kick_feet", 8, 0.06f),
                new Shot(11, 2.3f, "11_sprint_wave_feet", 8, 0.06f),
                new Shot(11, 2.75f, "11_sprint_wave_body", 8, 0.035f, body: true),
                new Shot(11, 3.02f, "11_sprint_wave_paws", 8, 0.05f, paws: true),
                new Shot(11, 3.42f, "11_sprint_wave"),
                new Shot(15, 0.1f, "15_roll_supine_paws", 8, 0.05f, paws: true),
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
            var sea = OtterTestSceneSetup.FindSea();
            var water = sea != null && sea.TryGetComponent<Renderer>(out var surface) && surface.enabled ? new[] { surface } : new Renderer[0];
            // the forepaws work under the chest, where the sea's surface would hide them
            var scene = body.Concat(reference).Concat(shot.paws ? new Renderer[0] : water).Concat(overlay).Distinct().ToArray();
            // from above and behind the left side, as the Scene view usually looks on
            Vector3 aside = (-right * 0.75f - forward * 0.45f + Vector3.up * 0.5f).normalized;
            string dir = Path.Combine("Logs", "Snapshots");
            Directory.CreateDirectory(dir);

            if (shot.frames <= 1)
            {
                var views = new[]
                {
                    Orthographic("top", bounds.center, Vector3.up, forward, extent * 0.8f, extent),
                    Orthographic("left", bounds.center, -right, Vector3.up, extent * 0.8f, extent, waterline: true),
                    Orthographic("back", bounds.center, -forward, Vector3.up, extent * 0.5f, extent, waterline: true),
                };
                Render(body, views, 1000, 800,
                    (v, tex) => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{views[v].name}.png"), tex.EncodeToPNG()));
                var persp = new[] { Perspective("persp", bounds.center, aside, extent * 0.9f) };
                Render(scene, persp, 1000, 800,
                    (v, tex) => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_{persp[v].name}.png"), tex.EncodeToPNG()));
                var controls = otter.GetComponent<OtterControls>();
                var screen = controls != null ? controls.PointerCamera : null;
                if (screen != null)
                    Capture(screen, 1000, 800, tex => File.WriteAllBytes(Path.Combine(dir, $"{label}_{shot.name}_game.png"), tex.EncodeToPNG()));
                return;
            }

            var girdle = OtterRig.FindActiveBone(otter, shot.paws || shot.body ? OtterRig.Chest : OtterRig.Hip);
            if (girdle == null) return;
            if (frame == 0)
            {
                var chains = OtterRig.FindLimbChains(otter);
                var pair = (shot.paws ? new[] { LimbId.FL, LimbId.FR } : new[] { LimbId.HL, LimbId.HR }).Select(id => chains[(int)id]).Where(c => c != null).ToArray();
                Vector3 ends = pair.Length > 0 ? pair.Aggregate(Vector3.zero, (s, c) => s + c.end.bone.position) / pair.Length : girdle.position;
                stripCenterLocal = girdle.InverseTransformPoint(shot.body ? bounds.center : Vector3.Lerp(girdle.position, ends, 0.6f));
                // the forepaw on the outside of the turn paddles it round
                var brain = otter.GetComponent<OtterLocomotionBrain>();
                outsideSign = brain != null && brain.Demand.yawRateTarget > 0f ? -1f : 1f;
                // held still in the world, framing the otter's whole way through the strip from its left
                var rb = otter.GetComponent<Rigidbody>();
                Vector3 travel = rb != null ? Vector3.ProjectOnPlane(rb.velocity, Vector3.up) * (shot.interval * (shot.frames - 1)) : Vector3.zero;
                Vector3 side = (-right + Vector3.up * 0.6f).normalized;
                float half = (0.5f * travel.magnitude + extent * 0.9f) * CellHeight / CellWidth;
                held = Perspective("world", bounds.center + 0.5f * travel, side, Mathf.Max(extent * 0.7f, half));
                Release();
                int rows = (shot.frames + Columns - 1) / Columns;
                sheets = Enumerable.Range(0, shot.body ? 2 : shot.paws ? 6 : 5).Select(_ => new Texture2D(CellWidth * Columns, CellHeight * rows, TextureFormat.RGB24, false)).ToArray();
            }
            if (sheets == null) return;
            // rolling over, the forepaw taking the roll's stroke is on the side the body rolls down onto
            var strokes = otter.GetComponent<LimbStrokeAnimator>();
            if (shot.paws && strokes != null && strokes.RollLead != 0) outsideSign = strokes.RollLead;
            Vector3 center = girdle.TransformPoint(stripCenterLocal);
            // looking back along the body at a slice through the shoulders and forepaws, from just ahead of them
            var arms = shot.paws
                ? OtterRig.FindLimbChains(otter).Where((c, i) => c != null && OtterRig.IsFore((LimbId)i))
                    .SelectMany(c => new[] { c.root.bone.position, c.end.bone.position }).Select(p => Vector3.Dot(p - center, forward)).DefaultIfEmpty(0f).ToArray()
                : new[] { 0f };
            float ahead = arms.Max() + 0.03f, slice = ahead - arms.Min() + 0.03f;
            var close = shot.body
                ? new[] { Orthographic("left", center, -right, Vector3.up, extent * 0.85f, extent, waterline: true) }
                : shot.paws
                ? new[]
                {
                    Orthographic("top", center, Vector3.up, forward, extent * 0.3f, extent),
                    Orthographic("below", center, Vector3.down, forward, extent * 0.3f, extent),
                    Orthographic("outside", center, right * outsideSign, Vector3.up, extent * 0.3f, extent),
                    new View("ahead", center + forward * ahead, Quaternion.LookRotation(-forward, Vector3.up), extent * 0.3f, false, depth: slice),
                }
                : new[]
                {
                    Orthographic("top", center, Vector3.up, forward, extent * 0.3f, extent),
                    Orthographic("back", center, -forward, Vector3.up, extent * 0.3f, extent, waterline: true),
                    Orthographic("left", center, -right, Vector3.up, extent * 0.3f, extent, waterline: true),
                };
            // the paddling paw from outside the turn, a little ahead and below, where the chest does not hide it
            Vector3 look = shot.paws ? (right * outsideSign * 0.8f + forward * 0.35f - Vector3.up * 0.25f).normalized : aside;
            var wide = shot.body ? new[] { held } : new[] { Perspective("persp", center, look, extent * 0.45f), held };
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

        static View Orthographic(string name, Vector3 center, Vector3 from, Vector3 up, float size, float extent, bool waterline = false) =>
            new View(name, center + from * (extent + 1f), Quaternion.LookRotation(-from, up), size, false, waterline);

        static View Perspective(string name, Vector3 center, Vector3 from, float size) =>
            new View(name, center + from * (size / Mathf.Tan(0.5f * FieldOfView * Mathf.Deg2Rad)), Quaternion.LookRotation(-from, Vector3.up), size, true);

        static void Capture(Camera cam, int width, int height, System.Action<Texture2D> use)
        {
            var rt = new RenderTexture(width, height, 24);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            var target = cam.targetTexture;
            cam.targetTexture = rt;
            try
            {
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                use(tex);
            }
            finally
            {
                cam.targetTexture = target;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }

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
            var sea = OtterTestSceneSetup.FindSea();
            var water = sea != null ? sea.GetComponent<Water>() : null;
            var seaMaterial = sea != null && sea.TryGetComponent<Renderer>(out var surface) ? surface.sharedMaterial : null;
            GameObject pane = null;
            try
            {
                for (int v = 0; v < views.Length; v++)
                {
                    cam.orthographic = !views[v].perspective;
                    cam.orthographicSize = views[v].size;
                    cam.farClipPlane = views[v].depth > 0f ? views[v].depth : 50f;
                    go.transform.SetPositionAndRotation(views[v].position, views[v].rotation);
                    if (views[v].waterline && water != null && seaMaterial != null)
                        pane = WaterPane(views[v], water.SurfaceY, (float)width / height, seaMaterial);
                    cam.Render();
                    if (pane != null) Object.DestroyImmediate(pane);
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
                if (pane != null) Object.DestroyImmediate(pane);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }

        // A level view's camera is inside the sea's box, whose faces show only from outside: a sheet of the sea just
        // in front of the camera, across the picture from the surface down past its foot, stands in for the water.
        static GameObject WaterPane(View view, float surfaceY, float aspect, Material material)
        {
            float bottom = view.position.y - 1.5f * view.size;
            if (surfaceY <= bottom) return null;
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.layer = Layer;
            float depth = surfaceY - bottom;
            Vector3 ahead = view.rotation * Vector3.forward * 0.5f;
            // the quad shows its face toward -z, so turned as the camera it faces it
            go.transform.SetPositionAndRotation(new Vector3(view.position.x, surfaceY - 0.5f * depth, view.position.z) + ahead, view.rotation);
            go.transform.localScale = new Vector3(2.5f * view.size * aspect, depth, 1f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }
    }
}
