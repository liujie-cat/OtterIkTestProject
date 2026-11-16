using System.Linq;
using OtterLocomotion.Testing;
using UnityEditor;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Enters Play, drives the active otter with a scripted input (OtterInputPlayback) while LocomotionTelemetry
    /// records, writes Logs/Locomotion_&lt;label&gt;_summary.txt + CSV and pictures at set moments (LocomotionSnapshots),
    /// then leaves Play. The otter gets fresh locomotion
    /// components for the run (code defaults, replacing the scene's for this Play only), so runs compare.
    /// The scene is never modified.
    /// </summary>
    [InitializeOnLoad]
    static class LocomotionPlaybackRunner
    {
        const string StateKey = "OtterLocomotion.Playback.State";   // 1 = entering Play, 2 = running
        const string LabelKey = "OtterLocomotion.Playback.Label";
        const string ScriptKey = "OtterLocomotion.Playback.Script";
        const string OldLimbsKey = "OtterLocomotion.Playback.OldLimbs";
        const double TimeoutSeconds = 180.0;

        static OtterInputPlayback playback;
        static LocomotionTelemetry telemetry;
        static double deadline;
        static LocomotionSnapshots.Shot[] shots = new LocomotionSnapshots.Shot[0];
        static int nextShot, nextFrame;

        static LocomotionPlaybackRunner() => EditorApplication.update += Tick;

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback")]
        static void RunBaseline() => Run("baseline", "baseline");

        [MenuItem("Tools/Otter Locomotion/Run Posture Playback")]
        static void RunPosture() => Run("posture", "posture");

        [MenuItem("Tools/Otter Locomotion/Run Turn Playback")]
        static void RunTurns() => Run("turns", "turns");

        [MenuItem("Tools/Otter Locomotion/Run Turn Playback (old limbs)")]
        static void RunTurnsOldLimbs() => Run("turns_old", "turns", oldLimbs: true);

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback (old limbs)")]
        static void RunBaselineOldLimbs() => Run("baseline_old", "baseline", oldLimbs: true);

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Posture Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Turn Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Turn Playback (old limbs)", true)]
        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback (old limbs)", true)]
        static bool CanRun() => !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <param name="oldLimbs">Leave the limbs to the scene's old drivers (no LimbStrokeAnimator), to compare against.</param>
        public static void Run(string label, string script, bool oldLimbs = false)
        {
            SessionState.SetString(LabelKey, label);
            SessionState.SetString(ScriptKey, script);
            SessionState.SetBool(OldLimbsKey, oldLimbs);
            SessionState.SetInt(StateKey, 1);
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
        }

        static void Tick()
        {
            int state = SessionState.GetInt(StateKey, 0);
            if (state == 0 || !EditorApplication.isPlaying) return;

            if (state == 1)
            {
                // a paused editor would hold the run at its first frame until the timeout, and so would the editor
                // losing focus while the project does not run in the background (for this Play only)
                EditorApplication.isPaused = false;
                Application.runInBackground = true;
                var movement = Object.FindObjectsByType<MovementControllerRB>(FindObjectsSortMode.None).FirstOrDefault(m => m.isActiveAndEnabled);
                if (movement == null)
                {
                    Debug.LogWarning("[LocomotionPlaybackRunner] No active MovementControllerRB in the scene.");
                    Stop();
                    return;
                }
                // always the code defaults, so runs compare; the scene's locomotion components are swapped out for this Play only
                var managed = OtterTestSceneSetup.Managed;
                for (int i = managed.Length - 1; i >= 0; i--)
                {
                    var saved = movement.GetComponent(managed[i].type);
                    if (saved != null) Object.DestroyImmediate(saved);
                }
                bool oldLimbs = SessionState.GetBool(OldLimbsKey, false);
                foreach (var (type, _) in managed)
                    if (!oldLimbs || type != typeof(LimbStrokeAnimator)) movement.gameObject.AddComponent(type);
                var host = new GameObject("LocomotionPlayback (runtime)");
                playback = host.AddComponent<OtterInputPlayback>();
                playback.movement = movement;
                playback.segments = OtterInputPlayback.Script(SessionState.GetString(ScriptKey, "baseline"));
                telemetry = host.AddComponent<LocomotionTelemetry>();
                telemetry.playback = playback;
                telemetry.movement = movement;
                telemetry.label = SessionState.GetString(LabelKey, "baseline");
                shots = LocomotionSnapshots.For(SessionState.GetString(ScriptKey, "baseline"));
                nextShot = nextFrame = 0;
                LocomotionSnapshots.Release();
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
            while (nextShot < shots.Length && playback.SegmentIndex > shots[nextShot].segment)
            {
                nextShot++;
                nextFrame = 0;
                LocomotionSnapshots.Release();
            }
            if (nextShot < shots.Length && playback.SegmentIndex == shots[nextShot].segment)
            {
                var shot = shots[nextShot];
                if (Time.time - playback.SegmentStartTime >= shot.time + nextFrame * shot.interval)
                {
                    LocomotionSnapshots.Take(telemetry.movement.transform, telemetry.label, shot, nextFrame);
                    if (++nextFrame >= shot.frames)
                    {
                        nextShot++;
                        nextFrame = 0;
                    }
                }
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
