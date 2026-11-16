using System.IO;
using System.Linq;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Orthographic pictures of the otter at chosen moments of a playback, from above (head up) and from its left,
    /// showing only its skinned meshes so the water and the scene do not hide the limbs. Written to Logs/Snapshots.
    /// </summary>
    static class LocomotionSnapshots
    {
        public readonly struct Shot
        {
            public readonly int segment;
            public readonly float time;
            public readonly string name;
            public Shot(int segment, float time, string name) { this.segment = segment; this.time = time; this.name = name; }
        }

        const int Layer = 31;

        /// <summary>Moments to capture: segment index and seconds into it, matching OtterInputPlayback's scripts.</summary>
        public static Shot[] For(string script) => script == "posture" ? new Shot[0] : new[]
        {
            new Shot(1, 1.5f, "1_swim_straight"),
            new Shot(2, 0.30f, "2_swim_turn_a"), new Shot(2, 0.42f, "2_swim_turn_b"), new Shot(2, 0.54f, "2_swim_turn_c"),
            new Shot(5, 0.30f, "5_sprint_turn_a"), new Shot(5, 0.45f, "5_sprint_turn_b"),
            new Shot(7, 0.15f, "7_pivot_a"), new Shot(7, 0.30f, "7_pivot_b"), new Shot(7, 0.45f, "7_pivot_c"),
        };

        public static void Take(Transform otter, string label, string name)
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

            var layers = meshes.Select(m => m.gameObject.layer).ToArray();
            foreach (var m in meshes) m.gameObject.layer = Layer;
            var go = new GameObject("LocomotionSnapshotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = extent * 0.8f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = extent * 2f + 2f;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.64f);
            var rt = new RenderTexture(1000, 800, 24);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            cam.targetTexture = rt;
            string dir = Path.Combine("Logs", "Snapshots");
            Directory.CreateDirectory(dir);
            try
            {
                var views = new (string view, Vector3 from, Vector3 up)[]
                {
                    ("top", Vector3.up, forward),
                    ("left", -right, Vector3.up),
                };
                foreach (var (view, from, up) in views)
                {
                    go.transform.SetPositionAndRotation(bounds.center + from * (extent + 1f), Quaternion.LookRotation(-from, up));
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(dir, $"{label}_{name}_{view}.png"), tex.EncodeToPNG());
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
