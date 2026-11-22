using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>One place to bring the test scene up to date, see what is off, and run the scripted playbacks.</summary>
    public class OtterTestScenePanel : EditorWindow
    {
        const string ResetSettingsPref = "OtterLocomotion.TestScene.ResetSettings";
        const string SavePref = "OtterLocomotion.TestScene.Save";
        static readonly (string label, string title)[] Playbacks = { ("baseline", "基线回放"), ("turns", "转向回放"), ("speeds", "速度回放") };

        List<OtterTestSceneSetup.Check> checks = new List<OtterTestSceneSetup.Check>();
        bool stale = true;
        string lastLog = "";
        Vector2 scroll;
        GUIStyle detailStyle;

        [MenuItem("Tools/Otter Locomotion/Test Scene Panel", priority = 0)]
        static void Open() => GetWindow<OtterTestScenePanel>("Otter 测试场景");

        static bool ResetSettings { get => EditorPrefs.GetBool(ResetSettingsPref, true); set => EditorPrefs.SetBool(ResetSettingsPref, value); }
        static bool Save { get => EditorPrefs.GetBool(SavePref, true); set => EditorPrefs.SetBool(SavePref, value); }

        void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkStale;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += MarkStale;
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            Undo.undoRedoPerformed -= MarkStale;
        }

        void OnFocus() => MarkStale();
        void MarkStale() { stale = true; Repaint(); }
        void OnPlayModeChanged(PlayModeStateChange _) => MarkStale();
        void OnSceneOpened(UnityEngine.SceneManagement.Scene s, OpenSceneMode m) => MarkStale();
        void OnSceneSaved(UnityEngine.SceneManagement.Scene s) => MarkStale();
        void OnChangesPublished(ref ObjectChangeEventStream stream) => MarkStale();

        void OnGUI()
        {
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            // recompute only on Layout, so Layout and Repaint draw the same rows; not in Play, where the runner swaps the brain
            if (stale && !playing && Event.current.type == EventType.Layout)
            {
                checks = OtterTestSceneSetup.Inspect(ResetSettings, Save);
                stale = false;
            }
            detailStyle ??= new GUIStyle(EditorStyles.label) { wordWrap = true };

            EditorGUILayout.HelpBox(
                "一键把测试场景调整到当前代码期望的状态：运动组件挂在海獭身上、连好引用、用代码默认值，海水就位，海獭浮在吃水位置。" +
                "每一步先检查，只改不对的地方，随时可以点。整个刷新是一次 Undo。",
                MessageType.Info);

            EditorGUILayout.LabelField("状态", EditorStyles.boldLabel);
            if (playing) EditorGUILayout.LabelField("Play 中，退出后刷新状态。", EditorStyles.miniLabel);
            foreach (var c in checks) DrawCheck(c);

            var sea = OtterTestSceneSetup.FindSea();
            if (sea != null && sea.TryGetComponent<MeshRenderer>(out var look))
            {
                bool hidden = EditorGUILayout.ToggleLeft(new GUIContent("隐藏海水（看水下的四肢）",
                    "只关掉水箱的显示，水体还在：浮力、入水深度照常。Play 中切换，退出 Play 后恢复；不在 Play 时切换要保存场景才留下。"), !look.enabled);
                if (hidden == look.enabled)
                {
                    if (!playing) Undo.RecordObject(look, hidden ? "Hide the sea" : "Show the sea");
                    look.enabled = !hidden;
                }
            }

            EditorGUILayout.Space();
            ResetSettings = EditorGUILayout.ToggleLeft(new GUIContent("运动组件参数恢复为代码默认值",
                "关掉则保留 Inspector 里对运动大脑、四肢划水、翻身同步的调参。回放测试总是用代码默认值。"), ResetSettings);
            Save = EditorGUILayout.ToggleLeft(new GUIContent("刷新后保存场景", "包括刷新前已有的未保存改动。"), Save);

            using (new EditorGUI.DisabledScope(playing))
            {
                bool allOk = checks.Count > 0 && checks.All(c => c.level == OtterTestSceneSetup.Level.Ok);
                if (GUILayout.Button(allOk ? "一键刷新测试场景（已是最新）" : "一键刷新测试场景", GUILayout.Height(34)))
                {
                    lastLog = OtterTestSceneSetup.Refresh(ResetSettings, Save);
                    MarkStale();
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("测试回放（Play 一段脚本输入，写 Logs/Locomotion_*，结束自动退出 Play）", EditorStyles.boldLabel);
                foreach (var (label, title) in Playbacks)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(title, GUILayout.Width(90))) { LocomotionPlaybackRunner.Run(label, label); GUIUtility.ExitGUI(); }
                        EditorGUILayout.LabelField(PlaybackResult(label), detailStyle);
                        string path = SummaryPath(label);
                        using (new EditorGUI.DisabledScope(!File.Exists(path)))
                            if (GUILayout.Button("打开", GUILayout.Width(44))) EditorUtility.OpenWithDefaultApp(Path.GetFullPath(path));
                    }
                }
            }

            if (lastLog.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("上次刷新", EditorStyles.boldLabel);
                scroll = EditorGUILayout.BeginScrollView(scroll);
                EditorGUILayout.TextArea(lastLog, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawCheck(OtterTestSceneSetup.Check c)
        {
            string icon = c.level == OtterTestSceneSetup.Level.Ok ? "TestPassed"
                : c.level == OtterTestSceneSetup.Level.Fix ? "console.warnicon.sml" : "console.erroricon.sml";
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(c.title, EditorStyles.boldLabel, GUILayout.Width(80));
                GUILayout.Label(c.detail, detailStyle);
            }
        }

        static string SummaryPath(string label) => Path.Combine("Logs", $"Locomotion_{label}_summary.txt");

        static string PlaybackResult(string label)
        {
            string path = SummaryPath(label);
            if (!File.Exists(path)) return "还没跑过";
            string feel = File.ReadLines(path).FirstOrDefault(l => l.StartsWith("feel targets met"));
            return $"{File.GetLastWriteTime(path):MM-dd HH:mm}  {feel ?? ""}";
        }
    }
}
