// Utility.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Updater
{
    public static class Utility
    {
        public static async Task<string> GetFileHashAsync(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = await Task.Run(() => sha256.ComputeHash(stream));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Computes the SHA256 hash of a byte array.
        /// </summary>
        /// <param name="data">The data to hash.</param>
        /// <returns>Lowercase hexadecimal SHA256 string.</returns>
        public static string GetBytesHash(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Builds the cmd.exe arguments of the restart: wait, move the new files over the installation, remove the temp
        /// folder and start the application. Both folders are passed without a trailing separator, because a backslash
        /// before the closing quote escapes it for robocopy (exit code 16, nothing is copied). The robocopy log and its
        /// exit code are written to the logs folder for <see cref="SelfUpdateGuard.ImportRestartReport"/>.
        /// </summary>
        /// <param name="currentDir">Installation folder (may end with a separator).</param>
        /// <param name="tempDir">Folder with the new files (may end with a separator).</param>
        /// <param name="exeName">File name of the executable to start.</param>
        public static string BuildRestartArguments(string currentDir, string tempDir, string exeName)
        {
            char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            currentDir = currentDir.TrimEnd(separators);
            tempDir = tempDir.TrimEnd(separators);
            string reportDir = Path.Combine(currentDir, "logs");
            string robocopyLog = Path.Combine(reportDir, SelfUpdateGuard.RobocopyLogFile);
            string exitCodeFile = Path.Combine(reportDir, SelfUpdateGuard.ExitCodeFile);
            // "call echo %^ERRORLEVEL%" reads the robocopy exit code when the line runs, not when cmd parses it
            return $"/C ping 127.0.0.1 -n 2 > nul & " +
                   $"robocopy \"{tempDir}\" \"{currentDir}\" /E /IS /MOVE /R:20 /W:1 /NP /UNICODE /LOG:\"{robocopyLog}\" > nul & " +
                   $"call echo %^ERRORLEVEL% > \"{exitCodeFile}\" & " +
                   $"rmdir /s /q \"{tempDir}\" & " +
                   $"start \"\" \"{Path.Combine(currentDir, exeName)}\"";
        }

        /// <summary>
        /// Restarts the application after a self-update: a hidden cmd.exe chain (no .bat file on disk) replaces the files
        /// and starts the new executable once this process has exited.
        /// </summary>
        /// <param name="currentDir">Installation folder.</param>
        /// <param name="tempDir">Folder with the new files.</param>
        /// <param name="exeName">File name of the executable to start.</param>
        public static void RestartApp(string currentDir, string tempDir, string exeName)
        {
            string cmdArgs = BuildRestartArguments(currentDir, tempDir, exeName);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WindowStyle = ProcessWindowStyle.Hidden, // Hide the black window
                CreateNoWindow = true,
                UseShellExecute = false
            });

            Application.Exit();
        }

        public static void DeletePath(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to delete '{fullPath}'. Error: {ex.Message}", ex);
            }
        }
    }
}