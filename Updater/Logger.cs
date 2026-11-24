// Logger.cs
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Updater
{
    /// <summary>
    /// Keeps a numbered history of log files: the current file becomes ".1", the previous ".1" becomes ".2", and so on.
    /// </summary>
    public static class LogRotation
    {
        /// <summary>
        /// Shifts the log history by one step and drops the oldest file.
        /// </summary>
        /// <param name="path">Path of the current log file (for example logs/sct_updater.log).</param>
        /// <param name="keep">Number of old files to keep (".1" .. ".keep").</param>
        public static void Rotate(string path, int keep)
        {
            string Numbered(int n) => Path.Combine(Path.GetDirectoryName(path),
                $"{Path.GetFileNameWithoutExtension(path)}.{n}{Path.GetExtension(path)}");

            if (File.Exists(Numbered(keep))) File.Delete(Numbered(keep));
            for (int n = keep - 1; n >= 1; n--)
            {
                if (File.Exists(Numbered(n))) File.Move(Numbered(n), Numbered(n + 1));
            }
            if (File.Exists(path)) File.Move(path, Numbered(1));
        }
    }

    /// <summary>
    /// A simple static logger that writes to logs/sct_updater.log. The previous runs are kept as sct_updater.1.log ..
    /// and every line carries the identifier of the run.
    /// </summary>
    public static class Log
    {
        private const int KeptLogs = 4;
        private static readonly string _logDir = Path.Combine(Application.StartupPath, "logs");
        private static readonly string _logPath = Path.Combine(_logDir, "sct_updater.log");
        private static readonly object _lock = new object();

        /// <summary>Short identifier of this run; it is written on every line.</summary>
        public static string RunId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        static Log()
        {
            try
            {
                Directory.CreateDirectory(_logDir);
                LogRotation.Rotate(_logPath, KeptLogs);
            }
            catch (Exception)
            {
                // Ignored
            }
            Info("Logger initialized.");
        }

        /// <summary>
        /// Writes the facts needed to read a log of another machine: versions, system, locale and folders.
        /// </summary>
        /// <param name="version">Version of the running Updater.</param>
        /// <param name="channel">Update channel in use.</param>
        public static void WriteHeader(string version, string channel)
        {
            var culture = CultureInfo.CurrentCulture;
            Info($"Updater {version} | channel {channel} | run {RunId} | pid {Process.GetCurrentProcess().Id}");
            Info($"System: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} | runtime: {RuntimeInformation.FrameworkDescription}");
            Info($"Locale: {culture.Name}, UI {CultureInfo.CurrentUICulture.Name}, ANSI cp {culture.TextInfo.ANSICodePage}, OEM cp {culture.TextInfo.OEMCodePage}");
            Info($"Install folder: {Application.StartupPath} | exe: {Application.ExecutablePath}");
        }

        private static void Write(string level, string message, string file, int line)
        {
            try
            {
                string shortFile = Path.GetFileName(file);
                string logMessage = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{RunId}] [{shortFile}:{line}] {message}{Environment.NewLine}";

                // Lock to prevent multiple threads writing at the same time
                lock (_lock)
                {
                    File.AppendAllText(_logPath, logMessage);
                }
            }
            catch (Exception)
            {
                // Silently fail
            }
        }

        public static void Info(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write("INFO", message, file, line);
        }

        public static void Debug(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write("DEBUG", message, file, line);
        }

        public static void Warn(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write("WARN", message, file, line);
        }

        /// <summary>
        /// Logs an error with the full exception: type, message, stack trace and inner exceptions.
        /// </summary>
        public static void Error(Exception ex, string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write("ERROR", $"{message} | {ex}", file, line);
        }
    }
}
