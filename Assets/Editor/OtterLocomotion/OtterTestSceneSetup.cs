using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using OtterIK.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Brings the test scene to what the current code expects: the sea otter rig in place of the cat, IK targets on
    /// the rest pose, the locomotion components on the swimming body with their references and code-default
    /// settings, and the old ring controls off. Every step checks first and changes only what is off, so it can be run
    /// at any time. One Undo step.
    /// </summary>
    static class OtterTestSceneSetup
    {
        internal const string ScenePath = "Assets/Scenes/TestLimbIK/TestAdvancedSpineSystem.unity";
        const string LogFile = "OtterTestScene_Refresh.txt";

        /// <summary>Components the new locomotion needs on the body, in the order they are added (the controls first: the brain finds them as it wakes).</summary>
        internal static readonly (Type type, string title)[] Managed =
        {
            (typeof(OtterControls), "操作"),
            (typeof(OtterLocomotionBrain), "运动大脑"),
            (typeof(LimbStrokeAnimator), "四肢划水"),
            (typeof(PostureFlipSync), "翻身同步"),
            (typeof(SpineKickWave), "脊柱踢水波"),
            (typeof(WebThrust), "脚蹼推水"),
            (typeof(BodyKeel), "身体侧向阻力"),
            (typeof(StrokePropulsion), "划水推进"),
            (typeof(HindLegColliders), "后腿碰撞体"),
            (typeof(TailRudder), "尾舵"),
            (typeof(SpineKickSway), "腰部摆动"),
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
        static void RefreshMenu() => Refresh(resetSettings: true, save: false);

        [MenuItem("Tools/Otter Locomotion/Report Test Scene")]
        static void ReportMenu()
        {
            var lines = Inspect(resetSettings: true, save: false).Select(c => $"{c.level,-7} {c.title}: {c.detail}");
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "OtterTestScene_Report.txt"), lines);
        }

        [MenuItem("Tools/Otter Locomotion/Refresh Test Scene (no save)", true)]
        [MenuItem("Tools/Otter Locomotion/Report Test Scene", true)]
        static bool NotPlaying() => !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>The swimming body: the active object with the old controller, which the new controls switch off.</summary>
        internal static MovementControllerRB FindBody() =>
            Object.FindObjectsByType<MovementControllerRB>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(m => m.gameObject.activeInHierarchy);

        static DualRingUIControllerRB RingUI(MovementControllerRB body) =>
            body.RingUI != null ? body.RingUI : Object.FindObjectsByType<DualRingUIControllerRB>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

        /// <summary>What of the old ring controls is still on: the controller, its ring UI.</summary>
        static List<string> OldControlsOn(MovementControllerRB body)
        {
            var on = new List<string>();
            if (body.enabled) on.Add("旧控制器 MovementControllerRB");
            var ring = RingUI(body);
            if (ring != null && ring.gameObject.activeSelf) on.Add($"环形 UI '{ring.name}'");
            return on;
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

            var otter = SeaOtterRigTransplant.FindOtterInstance();
            var cat = SeaOtterRigTransplant.FindCatInstance();
            if (otter != null && cat == null) checks.Add(new Check("海獭骨骼", Level.Ok, otter.name));
            else if (otter == null && cat != null) checks.Add(new Check("海獭骨骼", Level.Fix, $"还是猫模型 '{cat.name}'，刷新时移植到海獭"));
            else if (otter != null) checks.Add(new Check("海獭骨骼", Level.Blocked, $"猫 '{cat.name}' 和海獭 '{otter.name}' 同时激活，请先停用一个"));
            else checks.Add(new Check("海獭骨骼", Level.Blocked, "场景里没有激活的猫或海獭模型"));

            if (otter != null)
            {
                var off = SeaOtterRigTransplant.ChainsOffRest(otter);
                checks.Add(off.Count == 0
                    ? new Check("IK 静止姿态", Level.Ok, "所有目标点在末端关节上")
                    : new Check("IK 静止姿态", Level.Fix, "偏离: " + string.Join(", ", off)));
            }

            var body = FindBody();
            if (body != null)
            {
                var on = OldControlsOn(body);
                checks.Add(on.Count == 0
                    ? new Check("旧操作", Level.Ok, "环形控制器和环形 UI 已停用（文件保留）")
                    : new Check("旧操作", Level.Fix, "还开着: " + string.Join(", ", on) + "，刷新时停用"));
            }
            foreach (var (type, title) in Managed)
            {
                if (body == null) { checks.Add(new Check(title, Level.Blocked, "没有带 MovementControllerRB 的激活物体")); continue; }
                var component = body.GetComponent(type);
                if (component == null) { checks.Add(new Check(title, Level.Fix, $"'{body.name}' 上还没有，刷新时添加")); continue; }
                if (component is Behaviour b && !b.enabled)
                {
                    checks.Add(new Check(title, Level.Ok, $"在 '{body.name}' 上，已关闭（对比旧系统时用；刷新不会打开它）"));
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
            if (!IsTestSceneOpen)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "已取消：当前场景的改动未处理。";
                EditorSceneManager.OpenScene(ScenePath);
                log.AppendLine($"打开 {ScenePath}");
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Refresh otter test scene");

            var otter = SeaOtterRigTransplant.FindOtterInstance();
            var cat = SeaOtterRigTransplant.FindCatInstance();
            if (otter == null && cat != null)
            {
                var model = SeaOtterRigTransplant.LoadOtterModel();
                if (model == null) log.AppendLine("!! 找不到海獭模型资源，跳过移植");
                else
                {
                    log.AppendLine("== 移植猫骨骼到海獭").AppendLine(SeaOtterRigTransplant.Transplant(cat, model, alignSpine: true, fitTargets: true));
                    otter = SeaOtterRigTransplant.FindOtterInstance();
                }
            }
            else if (otter != null && cat != null) log.AppendLine($"!! 猫 '{cat.name}' 和海獭 '{otter.name}' 同时激活，没有改动骨骼");
            else if (otter != null) log.AppendLine($"海獭骨骼: {otter.name}，无需移植");
            else log.AppendLine("!! 场景里没有激活的猫或海獭模型");

            if (otter != null)
            {
                if (SeaOtterRigTransplant.ChainsOffRest(otter).Count > 0) log.AppendLine("== IK 归位").AppendLine(SeaOtterRigTransplant.Refit(otter));
                else log.AppendLine("IK 目标点都在静止姿态，无需归位");
            }

            var body = FindBody();
            if (body == null) log.AppendLine("!! 没有带 MovementControllerRB 的激活物体，跳过运动组件");
            else
            {
                if (body.enabled)
                {
                    Undo.RecordObject(body, "Retire ring controls");
                    body.enabled = false;
                    log.AppendLine("旧操作: 停用 MovementControllerRB");
                }
                var ring = RingUI(body);
                if (ring != null && ring.gameObject.activeSelf)
                {
                    Undo.RecordObject(ring.gameObject, "Retire ring controls");
                    ring.gameObject.SetActive(false);
                    log.AppendLine($"旧操作: 隐藏环形 UI '{ring.name}'");
                }
                foreach (var (type, title) in Managed)
                {
                    var component = body.GetComponent(type);
                    if (component == null)
                    {
                        component = Undo.AddComponent(body.gameObject, type);
                        log.AppendLine($"{title}: 添加到 '{body.name}'");
                    }
                    var wired = References(component, body, apply: true);
                    if (wired.Count > 0) log.AppendLine($"{title}: 连接引用 " + string.Join(", ", wired));
                    if (resetSettings)
                    {
                        var diffs = Differences(component, apply: true);
                        log.AppendLine(diffs.Count > 0 ? $"{title}: 恢复代码默认值 " + string.Join(", ", diffs) : $"{title}: 参数已是代码默认值");
                    }
                }
            }

            Undo.CollapseUndoOperations(group);

            var scene = SceneManager.GetActiveScene();
            if (save && scene.isDirty)
                log.AppendLine(EditorSceneManager.SaveScene(scene) ? "场景已保存" : "!! 场景保存失败");
            else if (scene.isDirty)
                log.AppendLine("场景有未保存的改动（这次不保存）");

            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", LogFile), log.ToString());
            return log.ToString();
        }

        /// <summary>
        /// Empty component references that can be filled: from the body, its children, or the only active one in the
        /// scene. Returns the field names; with <paramref name="apply"/> also fills them.
        /// </summary>
        static List<string> References(Component component, MovementControllerRB body, bool apply)
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
        static Component Defaults(Type type)
        {
            if (defaults.TryGetValue(type, out var c) && c != null) return c;
            var go = EditorUtility.CreateGameObjectWithHideFlags($"{type.Name} defaults", HideFlags.HideAndDontSave, type);
            go.SetActive(false);
            c = go.GetComponent(type);
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
