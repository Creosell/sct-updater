// UpdateService.App.cs
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SCT_Updater
{
    /// <summary>
    /// Installation of Python modules ("app" packages): module code under apps/&lt;module&gt;, its wheels unpacked next to the
    /// code, and the shared embeddable Python under apps/python_runtime.
    /// </summary>
    public partial class UpdateService
    {
        private const string WheelMarkerFile = ".sct_wheels.json";
        private const string PythonMarkerFile = ".sct_python.json";

        /// <summary>Module folders of the former ZIP layout that are removed after a module moves to the "app" package.</summary>
        private static readonly Dictionary<string, string[]> _legacyAppFolders = new Dictionary<string, string[]>
        {
            { "nextcloud_cli", new[] { "lib_nextcloud_cli" } },
            { "report_generator", new[] { "lib_report_generator" } }
        };

        private class WheelMarker
        {
            [JsonProperty("wheels")]
            public Dictionary<string, string> Wheels { get; set; } = new Dictionary<string, string>();
            [JsonProperty("files")]
            public List<string> Files { get; set; } = new List<string>();
        }

        private class PythonMarker
        {
            [JsonProperty("version")]
            public string Version { get; set; }
            [JsonProperty("sha256")]
            public string Sha256 { get; set; }
        }

        /// <summary>
        /// Installs or updates a Python module: fetches its manifest and the manifest of the installed version, then runs
        /// <see cref="InstallAppAsync"/>.
        /// </summary>
        /// <param name="product">Module to install at its latest version.</param>
        /// <param name="statusProgress">Receives status messages.</param>
        /// <param name="percentProgress">Receives download progress.</param>
        public async Task RunAppUpdateAsync(Product product, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Info($"Starting app update for {product.Id}...");
            FileManifest newManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, product.LatestVersion);

            FileManifest oldManifest = null;
            InstalledProduct installed = _localState.LoadLocalVersions().InstalledProducts.FirstOrDefault(p => p.Id == product.Id);
            if (installed != null && installed.Version != "Not Installed")
            {
                try
                {
                    oldManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, installed.Version);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Manifest of the installed version {installed.Version} is unavailable, installing without delta: {ex.Message}");
                }
            }

            await InstallAppAsync(newManifest, oldManifest, statusProgress, percentProgress);
        }

        /// <summary>
        /// Installs the Python core, the code files and the wheels of a module version.
        /// </summary>
        /// <param name="manifest">Manifest of the version to install.</param>
        /// <param name="oldManifest">
        /// Manifest of the installed version for a delta update; null for a clean install. A manifest of another package mode
        /// (the former ZIP layout) is treated as a clean install of the new layout.
        /// </param>
        /// <param name="statusProgress">Receives status messages.</param>
        /// <param name="percentProgress">Receives download progress.</param>
        public async Task InstallAppAsync(FileManifest manifest, FileManifest oldManifest, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            statusProgress.Report("Checking the Python runtime...");
            await EnsurePythonCoreAsync(manifest.Python, statusProgress, percentProgress);

            var oldFiles = new Dictionary<string, string>();
            if (oldManifest != null && oldManifest.IsApp)
            {
                oldFiles = oldManifest.Files.ToDictionary(f => f.Path, f => f.Hash);
                var obsolete = oldFiles.Keys.Except(manifest.Files.Select(f => f.Path)).ToList();
                if (obsolete.Any())
                {
                    statusProgress.Report($"Deleting {obsolete.Count} obsolete files...");
                    DeleteModuleFiles(obsolete);
                }
            }
            else if (oldManifest != null)
            {
                Log.Info($"Migrating {manifest.ProductId} from the '{oldManifest.PackageMode}' package to the app layout.");
                statusProgress.Report("Removing the previous package layout...");
                await DeleteModuleFilesFromManifestAsync(manifest);
            }

            await StartModuleUpdate_Files(manifest, oldFiles, statusProgress, percentProgress);

            statusProgress.Report("Installing libraries...");
            await SyncWheelsAsync(manifest, percentProgress);
            CleanupLegacyAppFolders(manifest.ProductId);
        }

        /// <summary>
        /// Makes sure the embeddable Python of the required minor version is installed in apps/python_runtime.
        /// </summary>
        /// <param name="minor">Python minor version of the module, e.g. "3.13".</param>
        /// <param name="statusProgress">Receives status messages.</param>
        /// <param name="percentProgress">Receives download progress.</param>
        /// <exception cref="InvalidDataException">The downloaded archive does not match the published SHA256.</exception>
        private async Task EnsurePythonCoreAsync(string minor, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            if (string.IsNullOrEmpty(minor)) throw new InvalidDataException("The module manifest does not name a Python version.");

            PythonCoreInfo core = await _nextcloudClient.GetPythonCoreAsync(minor);
            string target = Path.Combine(Application.StartupPath, AppConfig.PYTHON_RUNTIME_DIR);
            string marker = Path.Combine(target, PythonMarkerFile);

            if (File.Exists(marker))
            {
                var installed = JsonConvert.DeserializeObject<PythonMarker>(File.ReadAllText(marker));
                if (installed != null && string.Equals(installed.Sha256, core.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug($"Python {core.Version} is already installed.");
                    return;
                }
            }

            statusProgress.Report($"Downloading Python {core.Version}...");
            string archive = Path.Combine(Path.GetTempPath(), $"sct_python_{core.Sha256.Substring(0, 12)}.zip");
            await _nextcloudClient.DownloadFileAsync(_nextcloudClient.BuildPythonArchiveUrl(core), archive, percentProgress);

            try
            {
                if (!(await Utility.GetFileHashAsync(archive)).Equals(core.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Hash mismatch for the Python archive '{core.File}'.");
                }

                string staging = target + ".new";
                Utility.DeletePath(staging);
                await Task.Run(() => ExtractZipSafely(archive, staging));
                File.WriteAllText(Path.Combine(staging, PythonMarkerFile),
                    JsonConvert.SerializeObject(new PythonMarker { Version = core.Version, Sha256 = core.Sha256 }));

                await RunWithFileInUseHandlingAsync(() =>
                {
                    Utility.DeletePath(target);
                    Directory.Move(staging, target);
                    return Task.CompletedTask;
                }, "python_runtime");
            }
            finally
            {
                if (File.Exists(archive)) File.Delete(archive);
            }
        }

        /// <summary>
        /// Brings the wheels unpacked in apps/&lt;module&gt; in line with the manifest: downloads the missing ones into the
        /// local wheel cache, verifies them and unpacks them. Does nothing when the installed wheels already match.
        /// </summary>
        /// <param name="manifest">Manifest of the module version.</param>
        /// <param name="percentProgress">Receives download progress.</param>
        /// <exception cref="InvalidDataException">A downloaded wheel does not match the manifest SHA256.</exception>
        private async Task SyncWheelsAsync(FileManifest manifest, IProgress<int> percentProgress)
        {
            string appDir = Path.Combine(Application.StartupPath, AppConfig.APPS_DIR, manifest.ProductId);
            string markerPath = Path.Combine(appDir, WheelMarkerFile);
            List<ManifestWheel> wanted = manifest.Wheels ?? new List<ManifestWheel>();

            WheelMarker marker = File.Exists(markerPath) ? JsonConvert.DeserializeObject<WheelMarker>(File.ReadAllText(markerPath)) : null;
            var wantedMap = wanted.ToDictionary(w => w.File, w => w.Sha256.ToLowerInvariant());
            if (marker != null && marker.Wheels.Count == wantedMap.Count
                && marker.Wheels.All(w => wantedMap.TryGetValue(w.Key, out string hash) && hash == w.Value))
            {
                Log.Debug($"Wheels of {manifest.ProductId} are up to date.");
                return;
            }

            string cacheDir = Path.Combine(Application.StartupPath, AppConfig.WHEELHOUSE_DIR);
            Directory.CreateDirectory(cacheDir);
            string wheelsUrl = string.IsNullOrEmpty(manifest.WheelsUrl) ? AppConfig.DEFAULT_WHEELS_URL : manifest.WheelsUrl;

            int done = 0;
            var gate = new SemaphoreSlim(AppConfig.MAX_PARALLEL_DOWNLOADS);
            await Task.WhenAll(wanted.Select(async wheel =>
            {
                await gate.WaitAsync();
                try
                {
                    await EnsureCachedWheelAsync(wheel, wheelsUrl, cacheDir);
                    percentProgress.Report((int)((double)Interlocked.Increment(ref done) / wanted.Count * 100));
                }
                finally
                {
                    gate.Release();
                }
            }));

            await RunWithFileInUseHandlingAsync(() =>
            {
                InstallWheels(wanted, cacheDir, appDir, marker, markerPath);
                return Task.CompletedTask;
            }, manifest.ProductId);
        }

        /// <summary>
        /// Downloads a wheel into the local cache unless a verified copy is already there.
        /// </summary>
        /// <param name="wheel">Wheel entry of the manifest.</param>
        /// <param name="wheelsUrl">Wheel store relative to the Updater root.</param>
        /// <param name="cacheDir">Local wheel cache folder.</param>
        private async Task EnsureCachedWheelAsync(ManifestWheel wheel, string wheelsUrl, string cacheDir)
        {
            string cached = Path.Combine(cacheDir, wheel.File);
            if (File.Exists(cached) && (await Utility.GetFileHashAsync(cached)).Equals(wheel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            byte[] data = await _nextcloudClient.DownloadFileBytesAsync(_nextcloudClient.BuildFullFileUrl(wheelsUrl, wheel.File));
            if (!Utility.GetBytesHash(data).Equals(wheel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Hash mismatch for the wheel '{wheel.File}'.");
            }

            string temp = cached + ".part";
            File.WriteAllBytes(temp, data);
            if (File.Exists(cached)) File.Delete(cached);
            File.Move(temp, cached);
        }

        /// <summary>
        /// Replaces the previously unpacked wheels of a module with the wanted ones and records what was written.
        /// </summary>
        /// <param name="wanted">Wheels that must be installed.</param>
        /// <param name="cacheDir">Local wheel cache folder.</param>
        /// <param name="appDir">Module folder the wheels are unpacked into.</param>
        /// <param name="previous">Marker of the previous installation; null when there is none.</param>
        /// <param name="markerPath">Path of the marker file to write.</param>
        private static void InstallWheels(List<ManifestWheel> wanted, string cacheDir, string appDir, WheelMarker previous, string markerPath)
        {
            Directory.CreateDirectory(appDir);
            foreach (string old in previous?.Files ?? new List<string>())
            {
                Utility.DeletePath(Path.Combine(appDir, old));
                RemoveEmptyParents(Path.Combine(appDir, old), appDir);
            }

            var marker = new WheelMarker();
            foreach (ManifestWheel wheel in wanted)
            {
                marker.Files.AddRange(WheelInstaller.ExtractWithPaths(Path.Combine(cacheDir, wheel.File), appDir));
                marker.Wheels[wheel.File] = wheel.Sha256.ToLowerInvariant();
            }
            File.WriteAllText(markerPath, JsonConvert.SerializeObject(marker));
        }

        /// <summary>
        /// Removes the empty folders above a deleted file, stopping before the given root.
        /// </summary>
        /// <param name="deletedPath">Full path of the file that was deleted.</param>
        /// <param name="root">Folder that is never removed.</param>
        private static void RemoveEmptyParents(string deletedPath, string root)
        {
            string stop = Path.GetFullPath(root).TrimEnd('\\', '/');
            string dir = Path.GetDirectoryName(Path.GetFullPath(deletedPath));
            while (dir != null && dir.Length > stop.Length && dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        /// <summary>
        /// Deletes the files of an "app" manifest: the whole apps/&lt;module&gt; folder and the files installed outside it.
        /// The shared Python runtime and other modules are never touched.
        /// </summary>
        /// <param name="manifest">Manifest of the installed version.</param>
        /// <param name="root">Application folder.</param>
        private static void DeleteAppFiles(FileManifest manifest, string root)
        {
            string prefix = $"{AppConfig.APPS_DIR}/{manifest.ProductId}/";
            Utility.DeletePath(Path.Combine(root, AppConfig.APPS_DIR, manifest.ProductId));
            foreach (ManifestFile file in manifest.Files.Where(f => !f.Path.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                Utility.DeletePath(Path.Combine(root, file.Path));
            }
        }

        /// <summary>
        /// Deletes the module folders left by the former ZIP layout of a module.
        /// </summary>
        /// <param name="productId">Product identifier.</param>
        private void CleanupLegacyAppFolders(string productId)
        {
            if (!_legacyAppFolders.TryGetValue(productId, out string[] folders)) return;
            foreach (string folder in folders)
            {
                Log.Debug($"Removing legacy folder: {folder}");
                Utility.DeletePath(Path.Combine(Application.StartupPath, folder));
            }
        }

        /// <summary>
        /// Extracts a zip archive into a folder, refusing entries that would leave it.
        /// </summary>
        /// <param name="archive">Path of the zip file.</param>
        /// <param name="destination">Target folder; created when missing.</param>
        /// <exception cref="IOException">An entry would be written outside the target folder.</exception>
        private static void ExtractZipSafely(string archive, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            using (ZipArchive zip = ZipFile.OpenRead(archive))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Attempted to extract file outside of the target directory.");
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }
        }
    }
}
