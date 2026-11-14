using System;
using System.IO;
using System.Linq;
using UnityEditor;

namespace OtterIK.EditorTools
{
    /// <summary>
    /// Runs editor menu items requested from outside the editor: write one menu path per line to
    /// Temp/EditorMenu.request and the editor executes them on its next poll (once a second, edit mode only),
    /// then writes Logs/EditorMenu.result. Lets build scripts and agents drive editor tools without a click.
    /// </summary>
    [InitializeOnLoad]
    static class EditorMenuRequest
    {
        const string RequestFile = "Temp/EditorMenu.request";
        const string ResultFile = "Logs/EditorMenu.result";
        static double nextPoll;

        static EditorMenuRequest() => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(RequestFile) || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var items = File.ReadAllLines(RequestFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            File.Delete(RequestFile);
            var lines = items.Select(item => $"{(EditorApplication.ExecuteMenuItem(item) ? "ok    " : "FAILED")} {item}").ToList();
            lines.Insert(0, $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Directory.CreateDirectory(Path.GetDirectoryName(ResultFile));
            File.WriteAllLines(ResultFile, lines);
        }
    }
}
