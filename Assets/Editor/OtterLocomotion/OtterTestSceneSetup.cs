using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Brings the test scene to what the current code expects: the locomotion components on the swimming body with
    /// their references and code-default settings, and a translucent box of sea water with the otter floating in it at
    /// its draft. Every step checks first and changes only what is off, so it can be run at any time. One Undo step.
    /// </summary>
    static class OtterTestSceneSetup
    {
        internal const string ScenePath = "Assets/Scenes/OtterDemo.unity";
        const string LogFile = "OtterTestScene_Refresh.txt";

        /// <summary>Components the new locomotion needs on the body, in the order they are added (the controls first: the brain finds them as it wakes).</summary>
        internal static readonly (Type type, string title)[] Managed =
        {
            (typeof(OtterControls), "操作"),
            (typeof(ControlsOverlay), "状态光圈和箭头"),
            (typeof(OtterLocomotionBrain), "运动大脑"),
            (typeof(LimbStrokeAnimator), "四肢划水"),
            (typeof(PostureFlipSync), "翻身同步"),
            (typeof(SpineKickWave), "脊柱踢水波"),
            (typeof(WebThrust), "脚蹼推水"),
            (typeof(BodyKeel), "身体侧向阻力"),
            (typeof(Buoyancy), "浮力吃水"),
            (typeof(StrokePropulsion), "划水推进"),
            (typeof(HindLegColliders), "后腿碰撞体"),
            (typeof(TailRudder), "尾舵"),
            (typeof(SpineKickSway), "腰部摆动"),
            (typeof(DolphinTrack), "海豚轨迹"),
            (typeof(NeckLook), "脖子看向"),
            (typeof(AimGuide), "脊柱瞄准点"),
            (typeof(WaterReference), "水中参照"),
        };

        internal enum Level { Ok, Fix, Blocked }

        internal readonly struct Check
        {
            public readonly string title;
            public readonly Level level;
            public readonly string detail;
            public Check(string title, Level level, string detail) { this.title = title; this.level = level; this.detail = detail; }
        }

        [MenuItem("Tools/Otter Locomotion/Refresh Test Scene (no save)")]
        static void RefreshMenu() => Debug.Log("[Otter 测试场景] " + Refresh(resetSettings: true, save: false));

        [MenuItem("Tools/Otter Locomotion/Report Test Scene")]
        static void ReportMenu()
        {
            var checks = Inspect(resetSettings: true, save: false);
            var lines = checks.Select(c => $"{c.level,-7} {c.title}: {c.detail}").ToArray();
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "OtterTestScene_Report.txt"), lines);
            int off = checks.Count(c => c.level != Level.Ok);
            Debug.Log($"[Otter 测试场景] 检查：{(off > 0 ? $"{off} 项要处理" : "全部正常")}\n" + string.Join("\n", lines));
        }

        [MenuItem("Tools/Otter Locomotion/Refresh Test Scene (no save)", true)]
        [MenuItem("Tools/Otter Locomotion/Report Test Scene", true)]
        static bool NotPlaying() => !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>The swimming body: the active rigidbody that carries the otter's skeleton.</summary>
        internal static Rigidbody FindBody() =>
            Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(r => OtterRig.FindActiveBone(r.transform, OtterRig.Hip) != null);

        const string SeaName = "Sea";
        const string SeaMaterialPath = "Assets/ArtResource/Sea/SeaWater.mat";
        /// <summary>The sea's width (m), far past where the playbacks swim, and its depth, down to the ground.</summary>
        const float SeaWidth = 100f, SeaDepth = 5f;

        /// <summary>The translucent box of sea water the refresh lays out, with the Water surface on it.</summary>
        internal static GameObject FindSea() => SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == SeaName);

        /// <summary>What is off about the sea; null when it is in place.</summary>
        static string SeaOff()
        {
            var sea = FindSea();
            if (sea == null) return "还没有";
            if (!sea.TryGetComponent<Water>(out _)) return "还是旧的海面平面";
            var r = sea.GetComponent<MeshRenderer>();
            return r == null ? $"'{SeaName}' 没有 MeshRenderer，删掉它刷新时重建" : r.sharedMaterial == null ? "没有材质" : null;
        }

        /// <summary>Lays out the sea; one in place of an old surface plane keeps its height, a new one has it at 0.</summary>
        static void MakeSea(Vector3 around)
        {
            var sea = FindSea();
            if (sea == null || !sea.TryGetComponent<Water>(out _))
            {
                float surfaceY = sea != null ? sea.transform.position.y : 0f;
                if (sea != null) Undo.DestroyObjectImmediate(sea);
                sea = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sea.name = SeaName;
                // only to look at and float in: nothing rests on it
                Object.DestroyImmediate(sea.GetComponent<Collider>());
                sea.transform.position = new Vector3(around.x, surfaceY - 0.5f * SeaDepth, around.z);
                sea.transform.localScale = new Vector3(SeaWidth, SeaDepth, SeaWidth);
                sea.AddComponent<Water>();
                var fresh = sea.GetComponent<MeshRenderer>();
                fresh.sharedMaterial = SeaMaterial();
                fresh.shadowCastingMode = ShadowCastingMode.Off;
                Undo.RegisterCreatedObjectUndo(sea, "Lay out the sea");
                return;
            }
            var r = sea.GetComponent<MeshRenderer>();
            if (r != null && r.sharedMaterial == null)
            {
                Undo.RecordObject(r, "Sea water material");
                r.sharedMaterial = SeaMaterial();
            }
        }

        // made once and kept as an asset, to be tuned by hand
        static Material SeaMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(SeaMaterialPath);
            if (m != null) return m;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            m = new Material(shader) { name = "SeaWater" };
            m.SetFloat("_Surface", 1f); // transparent
            m.SetFloat("_Blend", 0f); // alpha
            m.SetFloat("_Cull", 2f); // front faces: from outside the box, the top face over what is under the water
            m.SetColor("_BaseColor", new Color(0.06f, 0.36f, 0.46f, 0.55f));
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", 0.9f);
            BaseShaderGUI.SetMaterialKeywords(m);
            Directory.CreateDirectory(Path.GetDirectoryName(SeaMaterialPath));
            AssetDatabase.CreateAsset(m, SeaMaterialPath);
            return m;
        }

        /// <summary>How far the body may sit from its draft (m).</summary>
        const float DraftTolerance = 0.001f;

        /// <summary>The body's height floating still on the sea, its skin as posed now; null without the sea, its buoyancy or a skin.</summary>
        static float? FloatingHeight(Rigidbody body)
        {
            var sea = FindSea();
            if (sea == null || !sea.TryGetComponent<Water>(out var water) || !body.TryGetComponent<Buoyancy>(out var buoyancy)) return null;
            return Buoyancy.FloatingHeight(body.transform, water.SurfaceY, buoyancy.density);
        }

        static bool IsTestSceneOpen => SceneManager.GetActiveScene().path == ScenePath;

        internal static List<Check> Inspect(bool resetSettings, bool save)
        {
            var checks = new List<Check>();
            var scene = SceneManager.GetActiveScene();
            if (!IsTestSceneOpen)
            {
                checks.Add(new Check("场景", Level.Fix, $"当前是 '{scene.name}'，刷新时打开 {Path.GetFileNameWithoutExtension(ScenePath)}"));
                return checks;
            }
            checks.Add(new Check("场景", Level.Ok, scene.name));

            var body = FindBody();
            if (body != null)
            {
                int lost = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(body.gameObject);
                checks.Add(lost == 0
                    ? new Check("丢失脚本", Level.Ok, $"'{body.name}' 上没有")
                    : new Check("丢失脚本", Level.Fix, $"'{body.name}' 上有 {lost} 个脚本已删掉的组件，刷新时移除"));
                string seaOff = SeaOff();
                checks.Add(seaOff == null
                    ? new Check("海水", Level.Ok, $"半透明水箱 '{SeaName}'，水面在 {FindSea().GetComponent<Water>().SurfaceY:F2} m")
                    : new Check("海水", Level.Fix, seaOff + "，刷新时换成半透明水箱"));
                float? afloat = FloatingHeight(body);
                Vector3 at = body.transform.position;
                checks.Add(afloat == null
                    ? new Check("海獭高度", Level.Fix, "还算不出吃水位置（要海水、浮力组件和皮肤），刷新时补好再放")
                    : Mathf.Abs(at.y - afloat.Value) > DraftTolerance
                        ? new Check("海獭高度", Level.Fix, $"在 {at.y:F3} m，吃水位置在 {afloat:F3} m，刷新时放下去")
                        : new Check("海獭高度", Level.Ok, $"浮在吃水位置，'{body.name}' 在 ({at.x:F2}, {at.y:F3}, {at.z:F2}) m"));
            }
            foreach (var (type, title) in Managed)
            {
                if (body == null) { checks.Add(new Check(title, Level.Blocked, "没有带海獭骨骼的激活刚体")); continue; }
                var component = body.GetComponent(type);
                if (component == null) { checks.Add(new Check(title, Level.Fix, $"'{body.name}' 上还没有，刷新时添加")); continue; }
                if (component is Behaviour b && !b.enabled)
                {
                    checks.Add(new Check(title, Level.Ok, $"在 '{body.name}' 上，已关闭（刷新不会打开它）"));
                    continue;
                }
                var missing = References(component, body, apply: false);
                var diffs = Differences(component, apply: false);
                var problems = new List<string>();
                if (missing.Count > 0) problems.Add("引用未连: " + string.Join(", ", missing));
                if (diffs.Count > 0 && resetSettings) problems.Add("与代码默认值不同: " + string.Join(", ", diffs));
                string kept = diffs.Count > 0 && !resetSettings ? $"（保留场景调参: {string.Join(", ", diffs)}）" : "";
                checks.Add(problems.Count > 0
                    ? new Check(title, Level.Fix, string.Join("；", problems))
                    : new Check(title, Level.Ok, $"在 '{body.name}' 上{kept}"));
            }

            if (scene.isDirty)
                checks.Add(new Check("保存", save ? Level.Fix : Level.Ok, save ? "有未保存的改动，刷新后保存" : "有未保存的改动（刷新后不保存）"));
            else
                checks.Add(new Check("保存", Level.Ok, "已保存"));
            return checks;
        }

        internal static string Refresh(bool resetSettings, bool save)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Play 模式下不能刷新。";
            var log = new StringBuilder();
            int changes = 0;
            void Changed(string line) { log.AppendLine(line); changes++; }
            if (!IsTestSceneOpen)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "已取消：当前场景的改动未处理。";
                EditorSceneManager.OpenScene(ScenePath);
                Changed($"打开 {ScenePath}");
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Refresh otter test scene");

            var body = FindBody();
            if (body == null) log.AppendLine("!! 没有带海獭骨骼的激活刚体，跳过运动组件");
            else
            {
                int lost = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(body.gameObject);
                if (lost > 0)
                {
                    Undo.RegisterCompleteObjectUndo(body.gameObject, "Remove missing scripts");
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(body.gameObject);
                    Changed($"丢失脚本: 移除 '{body.name}' 上 {lost} 个脚本已删掉的组件");
                }
                // all on first: a reference can be to one added after it
                foreach (var (type, title) in Managed)
                {
                    if (body.GetComponent(type) != null) continue;
                    Undo.AddComponent(body.gameObject, type);
                    Changed($"{title}: 添加到 '{body.name}'");
                }
                foreach (var (type, title) in Managed)
                {
                    var component = body.GetComponent(type);
                    var wired = References(component, body, apply: true);
                    if (wired.Count > 0) Changed($"{title}: 连接引用 " + string.Join(", ", wired));
                    if (resetSettings)
                    {
                        var diffs = Differences(component, apply: true);
                        if (diffs.Count > 0) Changed($"{title}: 恢复代码默认值 " + string.Join(", ", diffs));
                        else log.AppendLine($"{title}: 参数已是代码默认值");
                    }
                }
                string seaOff = SeaOff();
                if (seaOff != null)
                {
                    MakeSea(body.transform.position);
                    Changed($"海水: {seaOff}，已换成半透明水箱，水面在 {FindSea().GetComponent<Water>().SurfaceY:F2} m（材质 {SeaMaterialPath}）");
                }
                else log.AppendLine("海水: 水箱已在");
                // after the components and the sea: the draft is the buoyancy's in the water
                float? afloat = FloatingHeight(body);
                Vector3 at = body.transform.position;
                if (afloat == null) log.AppendLine("!! 算不出海獭的吃水位置，高度没动");
                else if (Mathf.Abs(at.y - afloat.Value) > DraftTolerance)
                {
                    Undo.RecordObject(body.transform, "Float the otter at its draft");
                    body.transform.position = new Vector3(at.x, afloat.Value, at.z);
                    Changed($"海獭高度: 从 {at.y:F3} m 放到吃水位置 {afloat:F3} m");
                }
                else log.AppendLine("海獭高度: 已在吃水位置");
            }

            Undo.CollapseUndoOperations(group);

            var scene = SceneManager.GetActiveScene();
            if (save && scene.isDirty)
                log.AppendLine(EditorSceneManager.SaveScene(scene) ? "场景已保存" : "!! 场景保存失败");
            else if (scene.isDirty)
                log.AppendLine("场景有未保存的改动（这次不保存）");

            log.Insert(0, (changes > 0 ? $"刷新：改了 {changes} 处" : "刷新：场景已是最新，没有要改的") + "\n");
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", LogFile), log.ToString());
            return log.ToString();
        }

        /// <summary>
        /// Empty component references that can be filled: from the body, its children, or the only active one in the
        /// scene. Returns the field names; with <paramref name="apply"/> also fills them.
        /// </summary>
        static List<string> References(Component component, Component body, bool apply)
        {
            var names = new List<string>();
            var so = new SerializedObject(component);
            var it = so.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference || it.name == "m_Script" || it.objectReferenceValue != null) continue;
                var field = component.GetType().GetField(it.name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                // a Transform would always resolve to the body's own; those are bones and set by hand
                if (field == null || !typeof(Component).IsAssignableFrom(field.FieldType) || typeof(Transform).IsAssignableFrom(field.FieldType)) continue;
                var found = body.GetComponent(field.FieldType);
                if (found == null) found = body.GetComponentInChildren(field.FieldType);
                if (found == null)
                    found = Object.FindObjectsByType(field.FieldType, FindObjectsSortMode.None).OfType<Component>().FirstOrDefault(c => c.gameObject.activeInHierarchy);
                if (found == null) continue;
                names.Add(it.name);
                if (apply) it.objectReferenceValue = found;
            }
            if (apply && names.Count > 0) so.ApplyModifiedProperties();
            return names;
        }

        static readonly Dictionary<Type, Component> defaults = new Dictionary<Type, Component>();

        // Hidden, inactive instances hold the code defaults (field initializers), compared property by property.
        // Inactive before the component goes on, so it never wakes, not even when made as Play ends; awake, it would
        // build what its OnDestroy can only free in Play.
        static Component Defaults(Type type)
        {
            if (defaults.TryGetValue(type, out var c) && c != null) return c;
            var go = new GameObject($"{type.Name} defaults") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            c = go.AddComponent(type);
            c.hideFlags = HideFlags.HideAndDontSave;
            if (defaults.Count == 0)
                AssemblyReloadEvents.beforeAssemblyReload += () =>
                {
                    foreach (var d in defaults.Values.Where(d => d != null)) Object.DestroyImmediate(d.gameObject);
                    defaults.Clear();
                };
            defaults[type] = c;
            return c;
        }

        /// <summary>Top-level fields whose value differs from the code default (references excluded); optionally copies the defaults in.</summary>
        static List<string> Differences(Component component, bool apply)
        {
            var src = new SerializedObject(Defaults(component.GetType()));
            var dst = new SerializedObject(component);
            var diffs = new List<string>();
            var it = src.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference || it.name == "m_Enabled") continue;
                var current = dst.FindProperty(it.propertyPath);
                if (current != null && SerializedProperty.DataEquals(it, current)) continue;
                diffs.Add(it.name);
                if (apply) dst.CopyFromSerializedProperty(it);
            }
            if (apply && diffs.Count > 0) dst.ApplyModifiedProperties();
            return diffs;
        }
    }
}
