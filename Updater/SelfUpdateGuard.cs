// SelfUpdateGuard.cs
using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Updater
{
    /// <summary>
    /// Detects a self-update that did not replace the Updater (it would restart and try again forever) and carries the
    /// result of the restart script from the process that started it to the next run.
    /// </summary>
    public static class SelfUpdateGuard
    {
        /// <summary>Name of the marker written before the restart.</summary>
        public const string MarkerFile = "self_update.json";
        /// <summary>Name of the robocopy log of the restart script (UTF-16).</summary>
        public const string RobocopyLogFile = "self_update_robocopy.log";
        /// <summary>Name of the file that receives the robocopy exit code.</summary>
        public const string ExitCodeFile = "self_update_exit.txt";

        private static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(30);
        private const int MaxReportLines = 40;
        private const int RobocopyFailureCode = 8;

        private class Attempt
        {
            [JsonProperty("from")] public string From { get; set; }
            [JsonProperty("to")] public string To { get; set; }
            [JsonProperty("at")] public DateTime At { get; set; }
        }

        /// <summary>Returns the folder that holds the marker and the restart report (logs next to the Updater).</summary>
        public static string DefaultFolder => Path.Combine(System.Windows.Forms.Application.StartupPath, "logs");

        /// <summary>
        /// Remembers that the Updater is about to restart to replace version <paramref name="from"/> with <paramref name="to"/>.
        /// </summary>
        /// <param name="from">Running version.</param>
        /// <param name="to">Version the restart is expected to install.</param>
        /// <param name="folder">Folder of the marker; the logs folder when null.</param>
        public static void Record(string from, string to, string folder = null)
        {
            folder = folder ?? DefaultFolder;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, MarkerFile),
                JsonConvert.SerializeObject(new Attempt { From = from, To = to, At = DateTime.Now }));
        }

        /// <summary>
        /// True when a recent restart was meant to move this very version to <paramref name="target"/> and the Updater is
        /// still on <paramref name="current"/>, so the replacement did not happen. A marker that is confirmed (current
        /// equals its target), expired or about other versions is removed and does not count.
        /// </summary>
        /// <param name="current">Running version.</param>
        /// <param name="target">Version the server asks for.</param>
        /// <param name="folder">Folder of the marker; the logs folder when null.</param>
        /// <param name="now">Current time; the system time when null.</param>
        public static bool HasFailedBefore(string current, string target, string folder = null, DateTime? now = null)
        {
            string path = Path.Combine(folder ?? DefaultFolder, MarkerFile);
            if (!File.Exists(path)) return false;

            Attempt attempt = null;
            try { attempt = JsonConvert.DeserializeObject<Attempt>(File.ReadAllText(path)); }
            catch (JsonException) { }

            bool failed = attempt != null && attempt.From == current && attempt.To == target
                          && (now ?? DateTime.Now) - attempt.At < AttemptLifetime;
            if (!failed) Clear(folder);
            return failed;
        }

        /// <summary>
        /// True when the marker says the restart was meant to reach <paramref name="current"/> from another version, that is,
        /// the replacement worked; the marker is removed.
        /// </summary>
        /// <param name="current">Running version.</param>
        /// <param name="folder">Folder of the marker; the logs folder when null.</param>
        public static bool ConfirmCompleted(string current, string folder = null)
        {
            string path = Path.Combine(folder ?? DefaultFolder, MarkerFile);
            if (!File.Exists(path)) return false;

            Attempt attempt = null;
            try { attempt = JsonConvert.DeserializeObject<Attempt>(File.ReadAllText(path)); }
            catch (JsonException) { }

            bool completed = attempt != null && attempt.To == current && attempt.From != current;
            Clear(folder);
            return completed;
        }

        /// <summary>Removes the marker.</summary>
        /// <param name="folder">Folder of the marker; the logs folder when null.</param>
        public static void Clear(string folder = null)
        {
            string path = Path.Combine(folder ?? DefaultFolder, MarkerFile);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>
        /// Logs the result of the previous restart script (robocopy exit code and log) and deletes the report files.
        /// </summary>
        /// <param name="folder">Folder of the report; the logs folder when null.</param>
        /// <returns>A one-line summary, or null when there was no restart report.</returns>
        public static string ImportRestartReport(string folder = null)
        {
            folder = folder ?? DefaultFolder;
            string exitPath = Path.Combine(folder, ExitCodeFile);
            string logPath = Path.Combine(folder, RobocopyLogFile);
            if (!File.Exists(exitPath) && !File.Exists(logPath)) return null;

            string exitText = File.Exists(exitPath) ? File.ReadAllText(exitPath).Trim() : "unknown";
            bool failed = !int.TryParse(exitText, out int code) || code >= RobocopyFailureCode;
            string summary = $"robocopy exit code {exitText}" + (failed ? " (the files were not replaced)" : " (ok)");
            if (failed) Log.Warn($"Previous self-update restart: {summary}");
            else Log.Info($"Previous self-update restart: {summary}");

            if (File.Exists(logPath))
            {
                var lines = File.ReadAllText(logPath, Encoding.Unicode).Split('\n')
                    .Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
                foreach (string line in lines.Skip(Math.Max(0, lines.Count - MaxReportLines)))
                    Log.Debug($"  robocopy: {line.Trim()}");
            }

            File.Delete(exitPath);
            File.Delete(logPath);
            return summary;
        }
    }
}
