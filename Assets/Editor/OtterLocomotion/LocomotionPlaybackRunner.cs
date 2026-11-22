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
        const double TimeoutSeconds = 180.0;

        static OtterInputPlayback playback;
        static LocomotionTelemetry telemetry;
        static double deadline;
        static LocomotionSnapshots.Shot[] shots = new LocomotionSnapshots.Shot[0];
        static int nextShot, nextFrame;

        static LocomotionPlaybackRunner() => EditorApplication.update += Tick;

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback")]
        static void RunBaseline() => Run("baseline", "baseline");

        [MenuItem("Tools/Otter Locomotion/Run Turn Playback")]
        static void RunTurns() => Run("turns", "turns");

        [MenuItem("Tools/Otter Locomotion/Run Speed Playback")]
        static void RunSpeeds() => Run("speeds", "speeds");

        [MenuItem("Tools/Otter Locomotion/Run Circle Playback")]
        static void RunCircles() => Run("circles", "circles");

        [MenuItem("Tools/Otter Locomotion/Run Dolphin Playback")]
        static void RunDolphin() => Run("dolphin", "dolphin");

        [MenuItem("Tools/Otter Locomotion/Run Dive Playback")]
        static void RunDive() => Run("dive", "dive");

        [MenuItem("Tools/Otter Locomotion/Run Baseline Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Turn Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Speed Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Circle Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Dolphin Playback", true)]
        [MenuItem("Tools/Otter Locomotion/Run Dive Playback", true)]
        static bool CanRun() => !EditorApplication.isPlayingOrWillChangePlaymode;

        public static void Run(string label, string script)
        {
            SessionState.SetString(LabelKey, label);
            SessionState.SetString(ScriptKey, script);
            SessionState.SetInt(StateKey, 1);
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
        }

        // before the scene's first frame: its own components would pose the rig in it, and the fresh ones capture the
        // limbs and the chest as they find them
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void BeforeFirstFrame()
        {
            if (SessionState.GetInt(StateKey, 0) == 1) Begin();
        }

        static void Begin()
        {
            // a paused editor would hold the run at its first frame until the timeout, and so would the editor
            // losing focus while the project does not run in the background (for this Play only)
            EditorApplication.isPaused = false;
            Application.runInBackground = true;
            var body = OtterTestSceneSetup.FindBody();
            if (body == null)
            {
                Debug.LogWarning("[LocomotionPlaybackRunner] No otter body (a Rigidbody carrying the otter's skeleton) in the scene.");
                Stop();
                return;
            }
            // always the code defaults, so runs compare; the scene's locomotion components are swapped out for this Play only
            var managed = OtterTestSceneSetup.Managed;
            for (int i = managed.Length - 1; i >= 0; i--)
            {
                var saved = body.GetComponent(managed[i].type);
                if (saved != null) Object.DestroyImmediate(saved);
            }
            foreach (var (type, _) in managed) body.gameObject.AddComponent(type);
            var controls = body.GetComponent<OtterControls>();
            var host = new GameObject("LocomotionPlayback (runtime)");
            playback = host.AddComponent<OtterInputPlayback>();
            playback.controls = controls;
            playback.segments = OtterInputPlayback.Script(SessionState.GetString(ScriptKey, "baseline"));
            telemetry = host.AddComponent<LocomotionTelemetry>();
            telemetry.playback = playback;
            telemetry.controls = controls;
            telemetry.label = SessionState.GetString(LabelKey, "baseline");
            shots = LocomotionSnapshots.For(SessionState.GetString(ScriptKey, "baseline"));
            nextShot = nextFrame = 0;
            LocomotionSnapshots.Release();
            deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            SessionState.SetInt(StateKey, 2);
        }

        static void Tick()
        {
            int state = SessionState.GetInt(StateKey, 0);
            if (state == 0 || !EditorApplication.isPlaying) return;

            if (state == 1)
            {
                Begin();
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
                    LocomotionSnapshots.Take(telemetry.controls.transform, telemetry.label, shot, nextFrame);
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
