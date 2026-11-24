// WheelInstaller.cs
using System;
using System.IO;
using System.IO.Compression;

namespace Updater
{
    /// <summary>
    /// Installs Python wheels (zip archives) into a site-packages folder by plain extraction,
    /// so the target machine needs neither pip nor uv.
    /// </summary>
    public static class WheelInstaller
    {
        private const string DataFolderSuffix = ".data";

        /// <summary>
        /// Extracts the importable content of a wheel (the package folders and its .dist-info) into a site-packages folder.
        /// Files under <c>*.data/purelib</c> and <c>*.data/platlib</c> go to the site-packages root;
        /// the other <c>*.data</c> folders (scripts, headers, data) are not installed.
        /// </summary>
        /// <param name="wheelPath">Path of the .whl file.</param>
        /// <param name="siteDir">Target site-packages folder; created when missing.</param>
        /// <returns>Number of files written.</returns>
        /// <exception cref="InvalidDataException">An entry would be written outside <paramref name="siteDir"/>.</exception>
        public static int Extract(string wheelPath, string siteDir) => ExtractWithPaths(wheelPath, siteDir).Count;

        /// <summary>
        /// Extracts a wheel like <see cref="Extract"/> and reports what was written.
        /// </summary>
        /// <param name="wheelPath">Path of the .whl file.</param>
        /// <param name="siteDir">Target folder; created when missing.</param>
        /// <returns>Paths of the written files relative to <paramref name="siteDir"/>, with forward slashes.</returns>
        /// <exception cref="InvalidDataException">An entry would be written outside <paramref name="siteDir"/>.</exception>
        public static System.Collections.Generic.List<string> ExtractWithPaths(string wheelPath, string siteDir)
        {
            string root = Path.GetFullPath(siteDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            var written = new System.Collections.Generic.List<string>();

            using (ZipArchive archive = ZipFile.OpenRead(wheelPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string relative = ToInstallPath(entry.FullName);
                    if (relative == null) continue;

                    string destination = Path.GetFullPath(Path.Combine(root, relative));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Wheel entry '{entry.FullName}' escapes the target folder.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, true);
                    written.Add(relative);
                }
            }
            return written;
        }

        /// <summary>
        /// Maps a wheel entry to its path inside site-packages.
        /// </summary>
        /// <param name="entryPath">Entry path inside the wheel, with forward slashes.</param>
        /// <returns>Relative install path, or null when the entry is not installed.</returns>
        private static string ToInstallPath(string entryPath)
        {
            string path = entryPath.Replace('\\', '/');
            int slash = path.IndexOf('/');
            string first = slash < 0 ? path : path.Substring(0, slash);

            if (!first.EndsWith(DataFolderSuffix, StringComparison.OrdinalIgnoreCase)) return path;

            string inner = slash < 0 ? string.Empty : path.Substring(slash + 1);
            foreach (string scheme in new[] { "purelib/", "platlib/" })
            {
                if (inner.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return inner.Substring(scheme.Length);
            }
            return null;
        }
    }
}
