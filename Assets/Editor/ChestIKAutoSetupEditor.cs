#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ChestIKAutoSetup))]
public class ChestIKAutoSetupEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // 先画原本的 Inspector（字段）
        DrawDefaultInspector();

        GUILayout.Space(12);

        // 分隔线
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        GUILayout.Space(6);

        // 拿到目标组件
        ChestIKAutoSetup setup = (ChestIKAutoSetup)target;

        // 按钮
        GUI.backgroundColor = new Color(0.3f, 0.9f, 0.6f);
        if (GUILayout.Button("Setup Advanced IK", GUILayout.Height(36)))
        {
            // 确保支持 Undo
            Undo.RecordObject(setup, "Setup Advanced IK");

            setup.Setup();

            // 标记场景脏，保证结果能保存
            EditorUtility.SetDirty(setup);
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(4);

        EditorGUILayout.HelpBox(
            "Click to auto-generate Targets & Poles and assign them to AdvancedTwoBoneIK.\n" +
            "This is an Editor-only button (no Canvas / UI required).",
            MessageType.Info
        );
    }
}
#endif
