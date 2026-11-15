using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OtterIK.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Brings the test scene to what the current code expects: the sea otter rig in place of the cat, IK targets on
    /// the rest pose, and the locomotion brain on the swimming body with its references and code-default settings.
    /// Every step checks first and changes only what is off, so it can be run at any time. One Undo step.
    /// </summary>
    static class OtterTestSceneSetup
    {
        internal const string ScenePath = "Assets/Scenes/TestLimbIK/TestAdvancedSpineSystem.unity";
        const string LogFile = "OtterTestScene_Refresh.txt";

        internal enum Level { Ok, Fix, Blocked }

        internal readonly struct Check
        {
            public readonly string title;
            public readonly Level level;
            public readonly string detail;
            public Check(string title, Level level, string detail) { this.title = title; this.level = level; this.detail = detail; }
        }

        [MenuItem("Tools/Otter Locomotion/Refresh Test Scene (no save)")]
        static void RefreshMenu() => Refresh(resetBrain: true, save: false);

        [MenuItem("Tools/Otter Locomotion/Report Test Scene")]
        static void ReportMenu()
        {
            var lines = Inspect(resetBrain: true, save: false).Select(c => $"{c.level,-7} {c.title}: {c.detail}");
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "OtterTestScene_Report.txt"), lines);
        }

        [MenuItem("Tools/Otter Locomotion/Refresh Test Scene (no save)", true)]
        [MenuItem("Tools/Otter Locomotion/Report Test Scene", true)]
        static bool NotPlaying() => !EditorApplication.isPlayingOrWillChangePlaymode;

        internal static MovementControllerRB FindBody() =>
            Object.FindObjectsByType<MovementControllerRB>(FindObjectsSortMode.None).FirstOrDefault(m => m.isActiveAndEnabled);

        static bool IsTestSceneOpen => SceneManager.GetActiveScene().path == ScenePath;

        internal static List<Check> Inspect(bool resetBrain, bool save)
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
            if (body == null) checks.Add(new Check("运动大脑", Level.Blocked, "没有激活的 MovementControllerRB"));
            else
            {
                var brain = body.GetComponent<OtterLocomotionBrain>();
                if (brain == null) checks.Add(new Check("运动大脑", Level.Fix, $"'{body.name}' 上还没有，刷新时添加"));
                else
                {
                    var missing = MissingReferences(brain, body);
                    var diffs = BrainDifferences(brain, apply: false);
                    var problems = new List<string>();
                    if (missing.Count > 0) problems.Add("引用未连: " + string.Join(", ", missing));
                    if (diffs.Count > 0 && resetBrain) problems.Add("与代码默认值不同: " + string.Join(", ", diffs));
                    string kept = diffs.Count > 0 && !resetBrain ? $"（保留场景调参: {string.Join(", ", diffs)}）" : "";
                    checks.Add(problems.Count > 0
                        ? new Check("运动大脑", Level.Fix, string.Join("；", problems))
                        : new Check("运动大脑", Level.Ok, $"在 '{body.name}' 上{kept}"));
                }
            }

            if (scene.isDirty)
                checks.Add(new Check("保存", save ? Level.Fix : Level.Ok, save ? "有未保存的改动，刷新后保存" : "有未保存的改动（刷新后不保存）"));
            else
                checks.Add(new Check("保存", Level.Ok, "已保存"));
            return checks;
        }

        internal static string Refresh(bool resetBrain, bool save)
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
            if (body == null) log.AppendLine("!! 没有激活的 MovementControllerRB，跳过运动大脑");
            else
            {
                var brain = body.GetComponent<OtterLocomotionBrain>();
                if (brain == null)
                {
                    brain = Undo.AddComponent<OtterLocomotionBrain>(body.gameObject);
                    log.AppendLine($"运动大脑: 添加到 '{body.name}'");
                }
                var missing = MissingReferences(brain, body);
                if (missing.Count > 0)
                {
                    var so = new SerializedObject(brain);
                    SetIfEmpty(so, nameof(OtterLocomotionBrain.body), body.GetComponent<Rigidbody>());
                    SetIfEmpty(so, nameof(OtterLocomotionBrain.intentSource), body);
                    SetIfEmpty(so, nameof(OtterLocomotionBrain.waterDrag), body.GetComponent<WaterDrag>());
                    so.ApplyModifiedProperties();
                    log.AppendLine("运动大脑: 连接引用 " + string.Join(", ", missing));
                }
                if (resetBrain)
                {
                    var diffs = BrainDifferences(brain, apply: true);
                    log.AppendLine(diffs.Count > 0 ? "运动大脑: 恢复代码默认值 " + string.Join(", ", diffs) : "运动大脑: 参数已是代码默认值");
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

        static void SetIfEmpty(SerializedObject so, string path, Object value)
        {
            var p = so.FindProperty(path);
            if (p.objectReferenceValue == null && value != null) p.objectReferenceValue = value;
        }

        static List<string> MissingReferences(OtterLocomotionBrain brain, MovementControllerRB body)
        {
            var missing = new List<string>();
            if (brain.body == null && body.GetComponent<Rigidbody>() != null) missing.Add(nameof(brain.body));
            if (brain.intentSource == null) missing.Add(nameof(brain.intentSource));
            if (brain.waterDrag == null && body.GetComponent<WaterDrag>() != null) missing.Add(nameof(brain.waterDrag));
            return missing;
        }

        static OtterLocomotionBrain defaults;

        // A hidden, inactive instance holds the code defaults (field initializers), compared property by property.
        static OtterLocomotionBrain Defaults()
        {
            if (defaults != null) return defaults;
            var go = EditorUtility.CreateGameObjectWithHideFlags("OtterLocomotionBrain defaults", HideFlags.HideAndDontSave, typeof(OtterLocomotionBrain));
            go.SetActive(false);
            defaults = go.GetComponent<OtterLocomotionBrain>();
            AssemblyReloadEvents.beforeAssemblyReload += () => { if (defaults != null) Object.DestroyImmediate(defaults.gameObject); };
            return defaults;
        }

        /// <summary>Top-level brain fields whose value differs from the code default (references excluded); optionally copies the defaults in.</summary>
        static List<string> BrainDifferences(OtterLocomotionBrain brain, bool apply)
        {
            var src = new SerializedObject(Defaults());
            var dst = new SerializedObject(brain);
            var diffs = new List<string>();
            var it = src.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference) continue;
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
