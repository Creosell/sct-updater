// UpdateService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SCT_Updater
{
    /// <summary>Decision of the user when a file of the module being updated is in use.</summary>
    public enum FileInUseChoice
    {
        /// <summary>Try the operation again (the application was closed or released the file).</summary>
        Retry,
        /// <summary>Abort the update.</summary>
        Cancel
    }

    public partial class UpdateService
    {
        private readonly NextcloudClient _nextcloudClient;
        private readonly LocalStateService _localState;
        private readonly SemaphoreSlim _fileInUseGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Called when a file cannot be written or deleted because it is in use. Receives the product identifier and the
        /// attempt number, and decides whether to retry or cancel. Calls are serialized. When null, the error is thrown as is.
        /// </summary>
        public Func<string, int, Task<FileInUseChoice>> FileInUseHandler { get; set; }

        private static readonly Dictionary<string, string[]> _legacyFiles = new Dictionary<string, string[]>
        {
            { "screen_checker", new[] { "WPF LCD Test.exe" } }
        };

        private void CleanupLegacyFiles(string productId)
        {
            if (!_legacyFiles.TryGetValue(productId, out string[] files)) return;
            string root = Application.StartupPath;
            foreach (string file in files)
            {
                Log.Debug($"Removing legacy file: {file}");
                Utility.DeletePath(Path.Combine(root, file));
            }
        }

        /// <summary>
        /// Determines whether a relative file path lies inside a folder excluded by convention
        /// (any path segment starting with an underscore, e.g. "_archive").
        /// </summary>
        private static bool IsExcludedConfigPath(string relativePath)
        {
            string[] segments = relativePath.Split('/', '\\');
            // Skip the last segment (the file name itself); only folder segments are checked.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].StartsWith("_", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Filters out relative config file paths that fall under an excluded ("_"-prefixed) folder.
        /// </summary>
        private static List<string> FilterExcludedConfigPaths(IEnumerable<string> relativePaths)
        {
            return relativePaths.Where(p => !IsExcludedConfigPath(p)).ToList();
        }

        public UpdateService(NextcloudClient nextcloudClient, LocalStateService localState)
        {
            _nextcloudClient = nextcloudClient;
            _localState = localState;
            Log.Debug("UpdateService initialized.");
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion)
        {
            Log.Debug("Checking for updates...");
            UpdateCheckResult result = new UpdateCheckResult();
            var productsToShow = new List<Product>();

            // --- 1. NEW: Check Device Configs First ---
            Log.Debug("Checking device configs...");
            try
            {
                var serverFiles = FilterExcludedConfigPaths(await _nextcloudClient.ListAllFilesRecursiveAsync(AppConfig.NC_DEVICE_CONFIGS_URL));
                string localConfigPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
                bool localFolderExists = Directory.Exists(localConfigPath);
                int localFileCount = 0;

                if (localFolderExists)
                {
                    var localRelativePaths = Directory.GetFiles(localConfigPath, "*", SearchOption.AllDirectories)
                        .Select(f => f.Substring(localConfigPath.Length).TrimStart('\\', '/'));
                    localFileCount = FilterExcludedConfigPaths(localRelativePaths).Count;
                }

                var configProduct = new Product
                {
                    Id = AppConfig.DEVICE_CONFIGS_ID,
                    Name = "Device configs",
                    InstalledVersion = "-", // Per request
                    LatestVersion = "-",    // Per request
                    IsUpdateAvailable = !localFolderExists || serverFiles.Count != localFileCount
                };
                productsToShow.Add(configProduct);
                Log.Debug($"Config check: Server={serverFiles.Count}, Local={localFileCount}, Update={configProduct.IsUpdateAvailable}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to check device configs. Skipping.");
            }

            // --- 2. Check Suite Manifest (existing logic) ---
            Log.Debug($"Checking suite manifest (channel: {(AppConfig.IsDevChannel ? "dev" : "stable")})...");
            SuiteManifest suiteManifest = await _nextcloudClient.GetSuiteManifestAsync();
            List<Product> channelProducts = suiteManifest.ResolveProducts(AppConfig.IsDevChannel);

            Product self = channelProducts.FirstOrDefault(p => p.Id == AppConfig.UPDATER_ID);
            if (self != null && self.LatestVersion != currentVersion)
            {
                Log.Warn($"Self-update required. Current: {currentVersion}, Server: {self.LatestVersion}");
                result.SelfUpdateProduct = self;
                return result; // Self-update takes precedence
            }

            Log.Debug("Self-update not required. Checking modules.");
            LocalVersions localData = _localState.LoadLocalVersions();

            var rebuiltChecks = new List<Task>();

            foreach (var serverProduct in channelProducts.Where(p => p.Id != AppConfig.UPDATER_ID))
            {
                InstalledProduct localProduct = localData.InstalledProducts.FirstOrDefault(p => p.Id == serverProduct.Id);
                if (localProduct != null)
                {
                    serverProduct.InstalledVersion = localProduct.Version;
                    serverProduct.IsUpdateAvailable = serverProduct.LatestVersion != localProduct.Version;

                    if (!serverProduct.IsUpdateAvailable && !string.IsNullOrEmpty(localProduct.ManifestHash))
                    {
                        rebuiltChecks.Add(MarkIfRebuiltAsync(serverProduct, localProduct.ManifestHash));
                    }
                }
                else
                {
                    serverProduct.InstalledVersion = "Not Installed";
                    serverProduct.IsUpdateAvailable = true;
                }
                productsToShow.Add(serverProduct);
            }

            await Task.WhenAll(rebuiltChecks);

            result.ProductsToShow = productsToShow;
            Log.Debug($"Found {productsToShow.Count} total items (configs + modules).");
            return result;
        }

        /// <summary>
        /// Compares the server file manifest hash with the installed one and flags the product as rebuilt
        /// (same version, different package). Fetch errors are logged and leave the product untouched.
        /// </summary>
        /// <param name="product">Server product whose version equals the installed one.</param>
        /// <param name="installedHash">Manifest hash recorded at install time.</param>
        private async Task MarkIfRebuiltAsync(Product product, string installedHash)
        {
            try
            {
                var (_, serverHash) = await _nextcloudClient.FetchFileManifestAsync(product.Id, product.LatestVersion);
                if (!serverHash.Equals(installedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"{product.Id} v{product.LatestVersion} was rebuilt on the server (manifest hash changed).");
                    product.IsRebuilt = true;
                    product.IsUpdateAvailable = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Rebuild check skipped for {product.Id}: {ex.Message}");
            }
        }

        // --- NEW: Method to sync device configs ---
        public async Task SyncDeviceConfigsAsync(IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Info("Starting device config sync...");
            string localPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
            string serverUrl = AppConfig.NC_DEVICE_CONFIGS_URL;

            // 1. Get server file list
            statusProgress.Report("Fetching server file list...");
            percentProgress.Report(0);
            var serverFiles = FilterExcludedConfigPaths(await _nextcloudClient.ListAllFilesRecursiveAsync(serverUrl));

            // --- CHANGED: Always clear local directory first for a clean sync ---
            statusProgress.Report("Clearing local config directory...");
            DeleteDeviceConfigs(); // This deletes the root folder

            if (!serverFiles.Any())
            {
                statusProgress.Report("No configs found on server. Local folder cleared.");
                Log.Warn("Device config sync: No files found on server. Local sync is complete (empty).");
                percentProgress.Report(100);
                return; // We are done, local folder is now empty, matching server.
            }

            // 3. Re-create directory and download
            Directory.CreateDirectory(localPath); // Re-create it
            Log.Debug($"Downloading {serverFiles.Count} config files...");

            int filesDone = 0;
            var semaphore = new SemaphoreSlim(AppConfig.MAX_PARALLEL_DOWNLOADS);
            var allTasks = new List<Task>();

            foreach (var relativePath in serverFiles)
            {
                var downloadTask = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string fileUrl = $"{serverUrl}/{relativePath}";
                        string localFilePath = Path.Combine(localPath, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));
                        byte[] data = await _nextcloudClient.DownloadFileBytesAsync(fileUrl);
                        File.WriteAllBytes(localFilePath, data); // Always overwrite

                        int done = Interlocked.Increment(ref filesDone);
                        percentProgress.Report((int)((double)done / serverFiles.Count * 100));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Failed to download config file {relativePath}");
                        // Continue with other files
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                allTasks.Add(downloadTask);
            }

            statusProgress.Report($"Downloading {serverFiles.Count} config files...");
            await Task.WhenAll(allTasks);

            percentProgress.Report(100);
            statusProgress.Report("Config sync complete.");
            Log.Info("Device config sync complete.");
        }

        // --- NEW: Method to delete all local configs ---
        public void DeleteDeviceConfigs()
        {
            Log.Info("Deleting local device configs folder...");
            string localPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
            try
            {
                Utility.DeletePath(localPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete device configs directory.");
                throw; // Re-throw to be caught by the UI
            }
        }


        public async Task ReinstallModuleAsync(Product product, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Info($"Starting re-install for {product.Id}...");
            statusProgress.Report($"Removing old files for {product.Name}...");

            var localData = _localState.LoadLocalVersions();
            var installedProduct = localData.InstalledProducts.FirstOrDefault(p => p.Id == product.Id);

            FileManifest oldManifest = null;
            if (installedProduct != null && installedProduct.Version != "Not Installed")
            {
                try
                {
                    // --- CHANGED: Use new GetFileManifestAsync signature ---
                    Log.Debug($"Fetching old manifest for deletion: {product.Id} v{installedProduct.Version}");
                    oldManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, installedProduct.Version);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to fetch old manifest. Cannot continue re-install.");
                    throw new Exception($"Failed to fetch the *old* manifest ({installedProduct.Version}) for deletion. Cannot continue re-install. Error: {ex.Message}", ex);
                }

                await DeleteModuleFilesFromManifestAsync(oldManifest);
            }
            _localState.SaveLocalVersion(product.Id, "Not Installed");

            statusProgress.Report("Fetching latest manifest...");
            // --- CHANGED: Use new GetFileManifestAsync signature ---
            FileManifest newManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, product.LatestVersion);

            statusProgress.Report($"Starting re-installation for {product.Name}...");
            if (newManifest.IsApp)
            {
                await InstallAppAsync(newManifest, null, statusProgress, percentProgress);
            }
            else if (newManifest.PackageMode == "zip")
            {
                await StartModuleUpdate_Zip(newManifest, statusProgress, percentProgress);
            }
            else
            {
                var emptyOldFiles = new Dictionary<string, string>();
                await StartModuleUpdate_Files(newManifest, emptyOldFiles, statusProgress, percentProgress);
            }
            Log.Info($"Re-install for {product.Id} complete.");
        }

        // ... (rest of UpdateService.cs remains the same) ...
        public async Task RunDeltaUpdateAsync(Product product, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Info($"Starting delta update for {product.Id}...");
            var localData = _localState.LoadLocalVersions();
            var installedProduct = localData.InstalledProducts.FirstOrDefault(p => p.Id == product.Id);

            if (installedProduct == null || installedProduct.Version == "Not Installed")
            {
                Log.Debug($"Product {product.Id} is not installed. Running fresh install.");
                // --- CHANGED: Use new GetFileManifestAsync signature ---
                FileManifest freshInstallManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, product.LatestVersion);
                var emptyOldFiles = new Dictionary<string, string>();
                await StartModuleUpdate_Files(freshInstallManifest, emptyOldFiles, statusProgress, percentProgress);
                return;
            }

            Log.Debug("Fetching new and old manifests for delta comparison...");
            // --- CHANGED: Use new GetFileManifestAsync signature ---
            FileManifest newManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, product.LatestVersion);

            statusProgress.Report("Fetching old manifest...");
            // --- CHANGED: Use new GetFileManifestAsync signature ---
            FileManifest oldManifest = await _nextcloudClient.GetFileManifestAsync(product.Id, installedProduct.Version);

            statusProgress.Report("Comparing versions...");
            var oldFiles = oldManifest.Files.ToDictionary(f => f.Path, f => f.Hash);
            var newFiles = newManifest.Files.ToDictionary(f => f.Path, f => f.Hash);

            var filesToDelete = oldFiles.Keys.Except(newFiles.Keys).ToList();
            if (filesToDelete.Any())
            {
                Log.Debug($"Deleting {filesToDelete.Count} obsolete files...");
                statusProgress.Report($"Deleting {filesToDelete.Count} obsolete files...");
                DeleteModuleFiles(filesToDelete);
            }

            await StartModuleUpdate_Files(newManifest, oldFiles, statusProgress, percentProgress);
            Log.Info($"Delta update for {product.Id} complete.");
        }

        private void DeleteModuleFiles(List<string> relativePaths)
        {
            string root = Application.StartupPath;
            foreach (var path in relativePaths)
            {
                try
                {
                    Utility.DeletePath(Path.Combine(root, path));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to delete old file {path}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Deletes all top-level files and folders of a manifest, asking the user what to do when one of them is in use.
        /// </summary>
        /// <param name="manifest">Manifest whose top-level items are removed.</param>
        /// <exception cref="OperationCanceledException">The user cancelled while a file was in use.</exception>
        public Task DeleteModuleFilesFromManifestAsync(FileManifest manifest) =>
            RunWithFileInUseHandlingAsync(() =>
            {
                DeleteModuleFilesFromManifest(manifest);
                return Task.CompletedTask;
            }, manifest?.ProductId);

        public void DeleteModuleFilesFromManifest(FileManifest manifest)
        {
            if (manifest?.Files == null) return;
            Log.Debug($"Deleting all files from manifest for {manifest.ProductId}...");
            string root = Application.StartupPath;

            if (manifest.IsApp)
            {
                DeleteAppFiles(manifest, root);
                return;
            }

            var topLevelItems = new HashSet<string>();
            foreach (var file in manifest.Files)
            {
                string topLevelItem = file.Path.Split(new[] { '/', '\\' }, 2)[0];
                topLevelItems.Add(topLevelItem);
            }

            foreach (string item in topLevelItems)
            {
                Log.Debug($"Deleting path: {item}");
                Utility.DeletePath(Path.Combine(root, item));
            }
        }

        public async Task StartModuleUpdate_Zip(FileManifest manifest, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Debug($"Starting ZIP update for {manifest.ProductId}...");

            var localData = _localState.LoadLocalVersions();
            var installed = localData.InstalledProducts.FirstOrDefault(p => p.Id == manifest.ProductId);
            if (installed != null && installed.Version != manifest.Version)
            {
                try
                {
                    statusProgress.Report("Fetching old manifest for cleanup...");
                    FileManifest oldManifest = await _nextcloudClient.GetFileManifestAsync(manifest.ProductId, installed.Version);
                    statusProgress.Report("Removing old version files...");
                    DeleteModuleFilesFromManifest(oldManifest);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Pre-cleanup skipped (old manifest unavailable): {ex.Message}");
                }
            }
            var fileToDownload = manifest.Files.FirstOrDefault();
            if (fileToDownload == null) throw new Exception("File manifest is empty.");

            string localDir = Application.StartupPath;
            string tempZipPath = Path.Combine(Path.GetTempPath(), fileToDownload.Path);

            if (File.Exists(tempZipPath))
            {
                string localHash = await Utility.GetFileHashAsync(tempZipPath);
                if (localHash.Equals(fileToDownload.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    statusProgress.Report("ZIP file already downloaded. Skipping...");
                }
                else
                {
                    Log.Debug("Temporary ZIP has wrong hash. Deleting.");
                    File.Delete(tempZipPath);
                }
            }

            if (!File.Exists(tempZipPath))
            {
                statusProgress.Report($"Downloading {fileToDownload.Path}...");
                Log.Debug($"Downloading new ZIP: {fileToDownload.Path}");
                string fullFileUrl = _nextcloudClient.BuildFullFileUrl(manifest.BaseUrl, fileToDownload.Path);
                await _nextcloudClient.DownloadFileAsync(fullFileUrl, tempZipPath, percentProgress);
            }

            statusProgress.Report($"Extracting {fileToDownload.Path}...");
            Log.Debug("Extracting ZIP...");
            try
            {
                await Task.Run(() =>
                {
                    using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string destinationPath = Path.GetFullPath(Path.Combine(localDir, entry.FullName));
                            if (!destinationPath.StartsWith(localDir, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new IOException("Attempted to extract file outside of the target directory.");
                            }
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destinationPath);
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                            entry.ExtractToFile(destinationPath, true);
                        }
                    }
                });
                File.Delete(tempZipPath);
                Log.Debug("Extraction complete. Temp ZIP deleted.");
                CleanupLegacyFiles(manifest.ProductId);
            }
            catch (InvalidDataException ex)
            {
                Log.Error(ex, "Failed to unzip file. It may be corrupt or not a ZIP.");
                string errorMsg = $"Failed to unzip file: {ex.Message}\n" +
                                  "Ensure you uploaded a .ZIP archive, not .7z or .RAR.\n\n" +
                                  $"File for inspection: {tempZipPath}";
                throw new Exception(errorMsg, ex);
            }
        }

        /// <summary>
        /// Downloads one manifest file to <paramref name="localPath"/>.
        /// </summary>
        /// <param name="manifest">Manifest the file belongs to.</param>
        /// <param name="file">File entry to download.</param>
        /// <param name="localPath">Destination path on disk.</param>
        /// <exception cref="OperationCanceledException">The user cancelled while the destination file was in use.</exception>
        private async Task DownloadManifestFileAsync(FileManifest manifest, ManifestFile file, string localPath)
        {
            byte[] data = await _nextcloudClient.DownloadFileBytesAsync(_nextcloudClient.BuildManifestFileUrl(manifest, file));

            await RunWithFileInUseHandlingAsync(() =>
            {
                File.WriteAllBytes(localPath, data);
                return Task.CompletedTask;
            }, manifest.ProductId);
        }

        /// <summary>
        /// Detects the errors Windows raises for a file held open by a running program.
        /// </summary>
        /// <param name="ex">Exception thrown by a file operation.</param>
        /// <returns>True for sharing/lock violations and access-denied errors, also when wrapped in another exception.</returns>
        private static bool IsFileInUse(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is UnauthorizedAccessException) return true;
                if (current is IOException && ((current.HResult & 0xFFFF) == 32 || (current.HResult & 0xFFFF) == 33)) return true;
            }
            return false;
        }

        /// <summary>
        /// Runs a file operation; when it fails because a file is in use, asks <see cref="FileInUseHandler"/>
        /// what to do and repeats the operation until it succeeds or the user cancels.
        /// </summary>
        /// <param name="action">File operation to run.</param>
        /// <param name="productId">Product being updated, passed to the handler.</param>
        /// <exception cref="OperationCanceledException">The handler returned <see cref="FileInUseChoice.Cancel"/>.</exception>
        private async Task RunWithFileInUseHandlingAsync(Func<Task> action, string productId)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (Exception ex) when (FileInUseHandler != null && IsFileInUse(ex))
                {
                    Log.Warn($"File in use while updating {productId} (attempt {attempt}): {ex.Message}");

                    FileInUseChoice choice;
                    await _fileInUseGate.WaitAsync();
                    try
                    {
                        choice = await FileInUseHandler(productId, attempt);
                    }
                    finally
                    {
                        _fileInUseGate.Release();
                    }

                    if (choice == FileInUseChoice.Cancel)
                    {
                        throw new OperationCanceledException($"Update of {productId} cancelled: a file is in use.", ex);
                    }
                    await Task.Delay(500);
                }
            }
        }

        public async Task StartModuleUpdate_Files(FileManifest newManifest, Dictionary<string, string> oldFiles, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Debug($"Starting FILES update for {newManifest.ProductId}...");
            int filesDone = 0;
            var semaphore = new SemaphoreSlim(AppConfig.MAX_PARALLEL_DOWNLOADS);
            var allDownloadTasks = new List<Task>();

            statusProgress.Report("Preparing to download files...");
            var filesToDownload = new List<ManifestFile>();

            // --- DELTA LOGIC ---
            foreach (var newFile in newManifest.Files)
            {
                if (oldFiles.TryGetValue(newFile.Path, out string oldHash) && oldHash == newFile.Hash)
                {
                    string localPath = Path.Combine(Application.StartupPath, newFile.Path);
                    if (File.Exists(localPath))
                    {
                        filesDone++;
                        continue;
                    }
                }
                filesToDownload.Add(newFile);
            }
            Log.Debug($"Found {filesToDownload.Count} files to download. {filesDone} files are already up-to-date.");

            if (!filesToDownload.Any())
            {
                statusProgress.Report("All files are up to date.");
                percentProgress.Report(100);
                return;
            }

            foreach (var file in filesToDownload)
            {
                var downloadTask = Task.Run(async () =>
                {
                    string localPath = Path.Combine(Application.StartupPath, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath));

                    await semaphore.WaitAsync();
                    try
                    {
                        await DownloadManifestFileAsync(newManifest, file, localPath);

                        int done = Interlocked.Increment(ref filesDone);
                        percentProgress.Report((int)((double)done / newManifest.Files.Count * 100));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                allDownloadTasks.Add(downloadTask);
            }

            statusProgress.Report($"Downloading {filesToDownload.Count} changed files...");
            await Task.WhenAll(allDownloadTasks);

            percentProgress.Report(100);
            statusProgress.Report("Download complete.");
        }

        public async Task StartSelfUpdateAsync(Product product, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Warn("Starting self-update...");
            // --- CHANGED: Use new GetFileManifestAsync signature ---
            FileManifest manifest = await _nextcloudClient.GetFileManifestAsync(product.Id, product.LatestVersion);
            string tempDir = Path.Combine(Path.GetTempPath(), "SCT_Updater_New");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            if (manifest.PackageMode == "zip")
                await StartSelfUpdate_Zip(manifest, tempDir, statusProgress, percentProgress);
            else
                await StartSelfUpdate_Files(manifest, tempDir, statusProgress, percentProgress);

            string exeName = Path.GetFileName(Application.ExecutablePath);
            string currentDir = Application.StartupPath;

            Log.Warn("Restarting via command line injection...");

            // Use the new fileless method
            Utility.RestartApp(currentDir, tempDir, exeName);
        }

        private async Task StartSelfUpdate_Zip(FileManifest manifest, string tempDir, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Debug("Self-update mode: ZIP");
            var fileToDownload = manifest.Files.FirstOrDefault();
            if (fileToDownload == null) throw new Exception("Self-update manifest is empty.");

            statusProgress.Report($"Downloading {fileToDownload.Path}...");
            string fullFileUrl = _nextcloudClient.BuildFullFileUrl(manifest.BaseUrl, fileToDownload.Path);
            string tempZipPath = Path.Combine(tempDir, fileToDownload.Path);

            await _nextcloudClient.DownloadFileAsync(fullFileUrl, tempZipPath, percentProgress);

            statusProgress.Report("Extracting update...");
            await Task.Run(() =>
            {
                using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(tempDir, entry.FullName));
                        if (!destinationPath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("Attempted to extract file outside of the target directory.");
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destinationPath);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        entry.ExtractToFile(destinationPath, true); // Overwrite
                    }
                }
            });
            File.Delete(tempZipPath);
            statusProgress.Report("Update downloaded and extracted.");
        }

        private async Task StartSelfUpdate_Files(FileManifest manifest, string tempDir, IProgress<string> statusProgress, IProgress<int> percentProgress)
        {
            Log.Debug("Self-update mode: FILES");
            int filesDone = 0;
            const int MAX_PARALLEL = 10;
            var semaphore = new SemaphoreSlim(MAX_PARALLEL);
            var allDownloadTasks = new List<Task>();

            foreach (var file in manifest.Files)
            {
                var downloadTask = Task.Run(async () => {
                    string localPath = Path.Combine(tempDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath));

                    await semaphore.WaitAsync();
                    try
                    {
                        await DownloadManifestFileAsync(manifest, file, localPath);

                        int done = Interlocked.Increment(ref filesDone);
                        percentProgress.Report((int)((double)done / manifest.Files.Count * 100));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                allDownloadTasks.Add(downloadTask);
            }

            statusProgress.Report($"Downloading {manifest.Files.Count} files...");
            await Task.WhenAll(allDownloadTasks);

            statusProgress.Report("Download complete.");
        }
    }
}
