using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OtterIK.EditorTools
{
    /// <summary>
    /// Dumps every active two-bone IK chain (joints, target, pole, bend) and the front stroke rest points to
    /// Logs/RigProbe_Edit.txt, or after two seconds of Play to Logs/RigProbe_Play.txt (then leaves Play mode).
    /// Offsets are in the girdle's spine bone space (chest for forelimbs, hip for hind limbs), in centimetres,
    /// so edit and play dumps compare directly.
    /// </summary>
    [InitializeOnLoad]
    static class RigProbe
    {
        const string DeadlineKey = "RigProbe.PlayDeadline";
        const float PlaySeconds = 2f;

        static RigProbe() => EditorApplication.update += Tick;

        [MenuItem("Tools/Sea Otter/Debug/Probe Limbs (Edit)")]
        static void ProbeEdit() => Write("RigProbe_Edit.txt");

        [MenuItem("Tools/Sea Otter/Debug/Probe Limbs (Play 2s)")]
        static void ProbePlay()
        {
            SessionState.SetFloat(DeadlineKey, -1f);
            EditorApplication.isPlaying = true;
        }

        static void Tick()
        {
            float deadline = SessionState.GetFloat(DeadlineKey, 0f);
            if (deadline == 0f || !EditorApplication.isPlaying) return;
            float now = (float)EditorApplication.timeSinceStartup;
            if (deadline < 0f) { SessionState.SetFloat(DeadlineKey, now + PlaySeconds); return; }
            if (now < deadline) return;
            SessionState.EraseFloat(DeadlineKey);
            Write("RigProbe_Play.txt");
            EditorApplication.isPlaying = false;
        }

        static string V(Vector3 v) => $"({v.x * 100f,6:F1},{v.y * 100f,6:F1},{v.z * 100f,6:F1})";

        static void Write(string file)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {(Application.isPlaying ? $"play, frame {Time.frameCount}" : "edit")}  (cm, in girdle spine-bone space)");
            var iks = Object.FindObjectsByType<AdvancedTwoBoneIK>(FindObjectsSortMode.None)
                .Where(ik => ik.isActiveAndEnabled && ik.root?.bone && ik.mid?.bone && ik.end?.bone)
                .OrderBy(ik => ik.name);
            foreach (var ik in iks)
            {
                var chest = FindAncestor(ik.root.bone, "spine_03.x") ?? FindAncestor(ik.root.bone, "root.x") ?? ik.root.bone.root;
                Vector3 L(Vector3 w) => chest.InverseTransformPoint(w);
                Vector3 a = ik.root.bone.position, b = ik.mid.bone.position, c = ik.end.bone.position;
                float l1 = Vector3.Distance(a, b) * 100f, l2 = Vector3.Distance(b, c) * 100f;
                float bend = 180f - Vector3.Angle(a - b, c - b);
                sb.AppendLine($"\n== {ik.name}  ({chest.name} space)  l1={l1:F1} l2={l2:F1}  elbow/knee bend={bend:F0} deg");
                sb.AppendLine($"  root  {ik.root.bone.name,-26} {V(L(a))}");
                sb.AppendLine($"  mid   {ik.mid.bone.name,-26} {V(L(b))}");
                sb.AppendLine($"  end   {ik.end.bone.name,-26} {V(L(c))}");
                if (ik.target)
                    sb.AppendLine($"  target {ik.target.name,-25} {V(L(ik.target.position))}  reach={Vector3.Distance(a, ik.target.position) * 100f:F1}  end-target={Vector3.Distance(c, ik.target.position) * 100f:F1}");
                if (ik.pole)
                    sb.AppendLine($"  pole  {ik.pole.name,-26} {V(L(ik.pole.position))}  parent={ik.pole.parent?.name}");
            }
            foreach (var tr in Object.FindObjectsByType<IKStrokeTrajectory_V2>(FindObjectsSortMode.None).Where(t => t.isActiveAndEnabled))
            {
                var chest = tr.transform;
                sb.AppendLine($"\n== IKStrokeTrajectory_V2 on {tr.name}: amp h={tr.strokeAmpHorizontal} up={tr.strokeAmpUp} fwd={tr.strokeAmpForward} restPhase={tr.restPhase01}");
                foreach (var rp in new[] { tr.leftRestPoint, tr.rightRestPoint })
                    if (rp) sb.AppendLine($"  rest  {rp.name,-26} {V(chest.InverseTransformPoint(rp.position))}  parent={rp.parent?.name} localPos={rp.localPosition:F4}");
                if (tr.source)
                    sb.AppendLine($"  shoulders L {V(chest.InverseTransformPoint(tr.source.leftShoulder.position))}  R {V(chest.InverseTransformPoint(tr.source.rightShoulder.position))}  armOffset={tr.source.armOffset}  outer L={tr.source.isLeftOuter} R={tr.source.isRightOuter}");
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", file), sb.ToString());
            Debug.Log($"[RigProbe] wrote Logs/{file}");
            Snapshot(Path.GetFileNameWithoutExtension(file));
        }

        // Orthographic close-ups of the chest from the belly, the left side and three-quarter front, framed in the
        // chest bone's space (+Y toward the head, +Z ventral) so they do not depend on the scene view.
        static void Snapshot(string prefix)
        {
            var front = Object.FindObjectsByType<IKStrokeTrajectory_V2>(FindObjectsSortMode.None).FirstOrDefault(t => t.isActiveAndEnabled);
            if (front == null) return;
            var chest = front.transform;
            Vector3 look = chest.TransformPoint(new Vector3(0f, 0.01f, 0.06f));
            var views = new (string name, Vector3 dir, Vector3 up)[]
            {
                ("belly", Vector3.forward, Vector3.up),
                ("side", Vector3.left, Vector3.back),
                ("threequarter", new Vector3(-0.8f, 0.9f, 0.7f), Vector3.back),
            };
            var go = new GameObject("RigProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 0.12f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.64f);
            var rt = new RenderTexture(900, 700, 24);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            cam.targetTexture = rt;
            foreach (var (name, dir, up) in views)
            {
                Vector3 d = chest.TransformDirection(dir.normalized);
                go.transform.SetPositionAndRotation(look + d * 1f, Quaternion.LookRotation(-d, chest.TransformDirection(up)));
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine("Logs", $"{prefix}_{name}.png"), tex.EncodeToPNG());
            }
            cam.targetTexture = null;
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        static Transform FindAncestor(Transform t, string name)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == name) return p;
            return null;
        }
    }
}
