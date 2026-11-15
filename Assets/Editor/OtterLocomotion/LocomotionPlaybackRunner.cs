using System.Linq;
using OtterLocomotion.Testing;
using UnityEditor;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Enters Play, drives the active otter with a scripted input (OtterInputPlayback) while LocomotionTelemetry
    /// records, writes Logs/Locomotion_&lt;label&gt;_summary.txt + CSV, then leaves Play. Adds an OtterLocomotionBrain
    /// at runtime if the otter has none, so the demand layer is recorded too. The scene is never modified.
    /// </summary>
    [InitializeOnLoad]
    static class LocomotionPlaybackRunner
    {
        const string StateKey = "OtterLocomotion.Playback.State";   // 1 = entering Play, 2 = running
        const string LabelKey = "OtterLocomotion.Playback.Label";
        const string ScriptKey = "OtterLocomotion.Playback.Script";
        const double TimeoutSeconds = 180.0;

        static OtterInputPlayback playback;
        static LocomotionTelemetry telemetry;
        static double deadline;

        static LocomotionPlaybackRunner() => EditorApplication.update += Tick;

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback")]
        static void RunBaseline() => Run("baseline", "baseline");

        [MenuItem("Tools/Otter Locomotion/Run Posture Playback")]
        static void RunPosture() => Run("posture", "posture");

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Posture Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Add Locomotion Brain To Otter", true)]
        static bool CanRun() => !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>Puts the (compute-only) brain on the otter so its gizmos show during manual Play. Undoable; not saved.</summary>
        [MenuItem("Tools/Otter Locomotion/Add Locomotion Brain To Otter")]
        static void AddBrain()
        {
            var movement = Object.FindObjectsByType<MovementControllerRB>(FindObjectsSortMode.None).FirstOrDefault(m => m.isActiveAndEnabled);
            if (movement == null)
            {
                Debug.LogWarning("[LocomotionPlaybackRunner] No active MovementControllerRB in the scene.");
                return;
            }
            if (movement.GetComponent<OtterLocomotionBrain>() == null) Undo.AddComponent<OtterLocomotionBrain>(movement.gameObject);
            Selection.activeGameObject = movement.gameObject;
        }

        public static void Run(string label, string script)
        {
            SessionState.SetString(LabelKey, label);
            SessionState.SetString(ScriptKey, script);
            SessionState.SetInt(StateKey, 1);
            EditorApplication.isPlaying = true;
        }

        static void Tick()
        {
            int state = SessionState.GetInt(StateKey, 0);
            if (state == 0 || !EditorApplication.isPlaying) return;

            if (state == 1)
            {
                var movement = Object.FindObjectsByType<MovementControllerRB>(FindObjectsSortMode.None).FirstOrDefault(m => m.isActiveAndEnabled);
                if (movement == null)
                {
                    Debug.LogWarning("[LocomotionPlaybackRunner] No active MovementControllerRB in the scene.");
                    Stop();
                    return;
                }
                if (movement.GetComponent<OtterLocomotionBrain>() == null) movement.gameObject.AddComponent<OtterLocomotionBrain>();
                var host = new GameObject("LocomotionPlayback (runtime)");
                playback = host.AddComponent<OtterInputPlayback>();
                playback.movement = movement;
                playback.segments = SessionState.GetString(ScriptKey, "baseline") == "posture"
                    ? OtterInputPlayback.PostureScript()
                    : OtterInputPlayback.BaselineScript();
                telemetry = host.AddComponent<LocomotionTelemetry>();
                telemetry.playback = playback;
                telemetry.movement = movement;
                telemetry.label = SessionState.GetString(LabelKey, "baseline");
                deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
                SessionState.SetInt(StateKey, 2);
                return;
            }

            if (playback == null || telemetry == null)
            {
                Debug.LogWarning("[LocomotionPlaybackRunner] Playback components were lost; aborting.");
                Stop();
                return;
            }
            bool timedOut = EditorApplication.timeSinceStartup > deadline;
            if (!playback.Finished && !timedOut) return;
            if (timedOut) Debug.LogWarning("[LocomotionPlaybackRunner] Timed out; writing what was recorded.");
            telemetry.WriteFiles();
            Stop();
        }

        static void Stop()
        {
            SessionState.EraseInt(StateKey);
            playback = null;
            telemetry = null;
            EditorApplication.isPlaying = false;
        }
    }
}
