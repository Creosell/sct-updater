// MainForm.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net; // For ServicePointManager
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SCT_Updater
    {
    public partial class MainForm : Form
        {
        // --- Services ---
        private readonly NextcloudClient _nextcloudClient;
        private readonly LocalStateService _localState;
        private readonly UpdateService _updateService;

        // --- State ---
        private bool _isUpdating = false;
        private List<Product> _allProducts = new List<Product>();

        public MainForm()
            {
            InitializeComponent();

            Log.Info("Application starting up.");
            ServicePointManager.DefaultConnectionLimit = AppConfig.MAX_PARALLEL_DOWNLOADS + 4;
            Log.Debug($"Connection limit set to {ServicePointManager.DefaultConnectionLimit}");

            try
                {
                AppConfig.LoadEnvConfiguration();
                Log.Debug(".env configuration loaded.");
                Log.WriteHeader(GetCurrentVersion(), AppConfig.IsDevChannel ? "dev" : "stable");
                SelfUpdateGuard.ImportRestartReport();
                }
            catch (Exception ex)
                {
                Log.Error(ex, "Failed to load .env configuration.");
                MessageBox.Show($"Critical error: {ex.Message}", ".env Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
                }

            _nextcloudClient = new NextcloudClient();
            _localState = new LocalStateService();
            _updateService = new UpdateService(_nextcloudClient, _localState) { FileInUseHandler = HandleFileInUseAsync };
            Log.Debug("Services initialized.");
            }

        /// <summary>
        /// Resolves a "file is in use" error: closes the running module applications on request,
        /// or lets the user close them manually, or cancels the update.
        /// </summary>
        /// <param name="productId">Product being updated.</param>
        /// <param name="attempt">Number of the failed attempt for the current operation.</param>
        /// <returns>Retry after the application was closed, Cancel to abort the update.</returns>
        private async Task<FileInUseChoice> HandleFileInUseAsync(string productId, int attempt)
            {
            List<Process> running = FindRunningModuleProcesses();
            try
                {
                // Nothing of ours holds the file: give a transient lock (e.g. antivirus scan) a few silent retries first
                if (!running.Any() && attempt <= 3) return FileInUseChoice.Retry;

                string apps = running.Any() ? string.Join(", ", running.Select(p => p.ProcessName).Distinct()) : "another program";
                string text = $"The application is currently in use ({apps}).\n\n" +
                              "Close it and continue the update?\n\n" +
                              "Yes - close the application and continue\n" +
                              "No - cancel the update";

                DialogResult answer = InvokeRequired
                    ? (DialogResult)Invoke(new Func<DialogResult>(() => MessageBox.Show(this, text, "Application in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)))
                    : MessageBox.Show(this, text, "Application in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes) return FileInUseChoice.Cancel;
                await Task.Run(() => CloseProcesses(running));
                return FileInUseChoice.Retry;
                }
            finally
                {
                foreach (var process in running) process.Dispose();
                }
            }

        /// <summary>
        /// Finds the running processes whose executable lives in the application folder, except the updater itself.
        /// </summary>
        /// <returns>Processes started from the application folder; the caller disposes them.</returns>
        private static List<Process> FindRunningModuleProcesses()
            {
            string root = Application.StartupPath.TrimEnd('\\', '/') + "\\";
            int ownId = Process.GetCurrentProcess().Id;
            var found = new List<Process>();

            foreach (var process in Process.GetProcesses())
                {
                try
                    {
                    if (process.Id != ownId && process.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                        found.Add(process);
                        continue;
                        }
                    }
                catch (Exception)
                    {
                    // Access to the module list of protected or exiting processes is denied; they cannot be ours
                    }
                process.Dispose();
                }
            return found;
            }

        /// <summary>
        /// Asks each process to close its main window and kills the ones that do not exit in time.
        /// </summary>
        /// <param name="processes">Processes to close.</param>
        private static void CloseProcesses(IEnumerable<Process> processes)
            {
            foreach (var process in processes)
                {
                try
                    {
                    Log.Warn($"Closing running application: {process.ProcessName} ({process.Id})");
                    process.CloseMainWindow();
                    if (!process.WaitForExit(3000))
                        {
                        process.Kill();
                        process.WaitForExit(3000);
                        }
                    }
                catch (Exception ex)
                    {
                    Log.Warn($"Could not close {process.ProcessName}: {ex.Message}");
                    }
                }
            }

        #region Form Load and UI Setup


        private async void MainForm_Load(object sender, EventArgs e)
            {
            if (_updateService == null)
                {
                // The .env configuration failed to load: no services exist, so nothing may contact the server
                Close();
                return;
                }

            this.Text = $"Updater {GetCurrentVersion()}{(AppConfig.IsDevChannel ? " [DEV]" : string.Empty)}";
            lblStatus.Text = "Configuring UI...";
            SetupDataGridView();
            dgvModules.DataSource = null;

            lblStatus.Text = "Checking for updates...";
            await RunUpdateCheck();
            }

        private void SetupDataGridView()
            {
            Log.Debug("Setting up DataGridView columns.");
            dgvModules.AutoGenerateColumns = false;
            dgvModules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvModules.RowHeadersVisible = false;
            dgvModules.AllowUserToAddRows = false;
            dgvModules.AllowUserToDeleteRows = false;
            dgvModules.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

            // --- Columns ---
            dgvModules.Columns.Add(new DataGridViewTextBoxColumn { Name = "ModuleName", HeaderText = "Module", DataPropertyName = "Name", ReadOnly = true, FillWeight = 150 });
            dgvModules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Installed", HeaderText = "Installed", DataPropertyName = "InstalledVersion", ReadOnly = true, FillWeight = 70 });
            dgvModules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Available", HeaderText = "Available", DataPropertyName = "LatestVersion", ReadOnly = true, FillWeight = 70 });

            // --- Re-install column (using your saved preference) ---
            var reloadColumn = new DataGridViewButtonColumn
                {
                Name = "ReinstallAction",
                HeaderText = "",
                Text = "⟳", // Default text
                UseColumnTextForButtonValue = false,
                Width = 40,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 30,
                DefaultCellStyle = new DataGridViewCellStyle
                    {
                    // Using your saved preference
                    Font = new Font(dgvModules.Font.FontFamily, 18, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Padding = new Padding(0)
                    }
                };
            dgvModules.Columns.Add(reloadColumn);

            // --- Update/Install column ---
            dgvModules.Columns.Add(new DataGridViewButtonColumn { Name = "UpdateAction", HeaderText = "", Text = "Update", UseColumnTextForButtonValue = false, FillWeight = 60 });
            }

        #endregion

        #region Update Check and UI State

        /// <summary>
        /// Runs the update check and refreshes the grid.
        /// </summary>
        /// <param name="offerRebuilt">When true, offers to reinstall products rebuilt on the server under the same version.</param>
        private async Task RunUpdateCheck(bool offerRebuilt = true)
            {
            SetUiState(isChecking: true);

            try
                {
                string currentVersion = GetCurrentVersion();
                var result = await _updateService.CheckForUpdatesAsync(currentVersion);

                if (result.SelfUpdateProduct == null && SelfUpdateGuard.ConfirmCompleted(currentVersion))
                    {
                    Log.Info($"Self-update to {currentVersion} completed.");
                    }

                if (result.SelfUpdateProduct != null && SelfUpdateGuard.HasFailedBefore(currentVersion, result.SelfUpdateProduct.LatestVersion))
                    {
                    string target = result.SelfUpdateProduct.LatestVersion;
                    Log.Error(new InvalidOperationException("Updater.exe was not replaced"),
                        $"Self-update {currentVersion} -> {target} did not replace the Updater; not retrying. See the restart report above.");
                    lblStatus.Text = "Self-update failed.";
                    MessageBox.Show($"The Updater was asked to switch from {currentVersion} to {target}, but the files were not replaced after the restart.\n\n" +
                                    "The update is not repeated. Send logs\\sct_updater.log and logs\\sct_updater.1.log to the developer.",
                                    "Self-update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                    }

                if (result.SelfUpdateProduct != null)
                    {
                    Log.Warn("Self-update required. Triggering.");
                    lblStatus.Text = "Updating the updater...";
                    MessageBox.Show($"A new version of the updater ({result.SelfUpdateProduct.LatestVersion}) is required. The application will now restart.", "Update Required", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // --- FIX: Use IProgress<T> interface ---
                    IProgress<string> statusProgress = new Progress<string>(status => lblStatus.Text = status);
                    IProgress<int> percentProgress = new Progress<int>(percent => UpdateProgressBar(percent));

                    await _updateService.StartSelfUpdateAsync(result.SelfUpdateProduct, currentVersion, statusProgress, percentProgress);
                    return;
                    }

                // NEW: Ensure configs are always first
                _allProducts = result.ProductsToShow
                    .OrderBy(p => p.Id == AppConfig.DEVICE_CONFIGS_ID ? 0 : 1)
                    .ThenBy(p => p.Name)
                    .ToList();

                BindingSource bs = new BindingSource { DataSource = _allProducts };
                dgvModules.DataSource = bs;
                UpdateRowButtons();
                lblStatus.Text = "Update check complete.";
                Log.Debug("Update check finished. UI refreshed.");
                }
            catch (Exception ex)
                {
                lblStatus.Text = "Error checking updates.";
                Log.Error(ex, "Failed to check for updates.");
                MessageBox.Show($"Could not check for updates: {ex.Message}\n\nURL: {AppConfig.SUITE_MANIFEST_URL}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            finally
                {
                SetUiState(isChecking: false);
                }

            if (offerRebuilt) await OfferRebuiltReinstallAsync();
            }

        /// <summary>
        /// Offers (default Yes) to reinstall products whose package was rebuilt on the server under the same version.
        /// </summary>
        private async Task OfferRebuiltReinstallAsync()
            {
            var rebuilt = _allProducts.Where(p => p.IsRebuilt).ToList();
            if (!rebuilt.Any()) return;

            string names = string.Join("\n", rebuilt.Select(p => $"• {p.Name} {p.LatestVersion}"));
            var answer = MessageBox.Show(
                $"These packages were rebuilt on the server under the same version:\n\n{names}\n\nReinstall them now?",
                "Package rebuilt",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes) return;

            SetUiState(isChecking: true);
            foreach (var product in rebuilt)
                {
                await RunUpdateTask(product, isReinstall: true);
                }
            SetUiState(isChecking: false);
            }

        private string GetCurrentVersion()
            {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
            }

        private void SetUiState(bool isChecking)
            {
            Log.Debug($"Setting UI state: isUpdating = {isChecking}");
            _isUpdating = isChecking;
            btnCheckUpdates.Enabled = !isChecking;
            btnUpdateAll.Enabled = !isChecking;
            dgvModules.Enabled = !isChecking;
            }


        private void UpdateRowButtons()
            {
            Log.Debug("Updating all row buttons...");
            string reloadSymbol = "⟳";

            foreach (DataGridViewRow row in dgvModules.Rows)
                {
                Product product = row.DataBoundItem as Product;
                if (product == null) continue;

                var updateButtonCell = row.Cells["UpdateAction"] as DataGridViewButtonCell;
                var reinstallCell = row.Cells["ReinstallAction"] as DataGridViewButtonCell;

                // --- NEW: Handle Device Configs Row ---
                if (product.Id == AppConfig.DEVICE_CONFIGS_ID)
                    {
                    string localConfigPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
                    bool localFolderExists = Directory.Exists(localConfigPath) && Directory.GetFiles(localConfigPath).Length > 0;

                    // 1. Configure "UpdateAction" (Install/Update/Checkmark)
                    if (product.IsUpdateAvailable)
                        {
                        updateButtonCell.Value = localFolderExists ? "Update" : "Install";
                        updateButtonCell.Style.BackColor = Color.LightGreen;
                        updateButtonCell.Style.ForeColor = Color.Black;
                        updateButtonCell.FlatStyle = FlatStyle.Popup;
                        }
                    else
                        {
                        updateButtonCell.Value = "✔";
                        updateButtonCell.Style.BackColor = Color.LightGray;
                        updateButtonCell.Style.ForeColor = Color.DarkGray;
                        updateButtonCell.FlatStyle = FlatStyle.Flat;
                        }

                    // 2. Configure "ReinstallAction" (Reload)
                    if (localFolderExists)
                        {
                        // Show the active reload symbol
                        reinstallCell.Value = reloadSymbol;
                        reinstallCell.Style.BackColor = SystemColors.Control;
                        reinstallCell.Style.ForeColor = Color.Black;
                        reinstallCell.FlatStyle = FlatStyle.Popup;
                        }
                    else
                        {
                        // Use inactive symbol
                        reinstallCell.Value = reloadSymbol;
                        reinstallCell.Style.BackColor = SystemColors.Control;
                        reinstallCell.Style.ForeColor = Color.Gainsboro; // Very light gray
                        reinstallCell.FlatStyle = FlatStyle.Flat;
                        }
                    }
                // --- ELSE: Handle Normal Module Rows ---
                else
                    {
                    // 1. Configure "UpdateAction" (Install/Update/Checkmark) button
                    if (product.IsUpdateAvailable)
                        {
                        updateButtonCell.Value = product.IsRebuilt ? "Rebuilt" : ( product.InstalledVersion == "Not Installed" ) ? "Install" : "Update";
                        updateButtonCell.Style.BackColor = Color.LightGreen;
                        updateButtonCell.Style.ForeColor = Color.Black;
                        updateButtonCell.FlatStyle = FlatStyle.Popup;
                        }
                    else
                        {
                        updateButtonCell.Value = "✔";
                        updateButtonCell.Style.BackColor = Color.LightGray;
                        updateButtonCell.Style.ForeColor = Color.DarkGray;
                        updateButtonCell.FlatStyle = FlatStyle.Flat;
                        }

                    // 2. Configure "ReinstallAction" (Reload) button
                    if (product.InstalledVersion == "Not Installed")
                        {
                        // Use inactive symbol
                        reinstallCell.Value = reloadSymbol;
                        reinstallCell.Style.BackColor = SystemColors.Control;
                        reinstallCell.Style.ForeColor = Color.Gainsboro; // Very light gray
                        reinstallCell.FlatStyle = FlatStyle.Flat;
                        }
                    else
                        {
                        // Show the active reload symbol
                        reinstallCell.Value = reloadSymbol;
                        reinstallCell.Style.BackColor = SystemColors.Control;
                        reinstallCell.Style.ForeColor = Color.Black;
                        reinstallCell.FlatStyle = FlatStyle.Popup;
                        }
                    }
                }
            }

        #endregion

        #region Button Click Handlers

        private async void btnCheckUpdates_Click(object sender, EventArgs e)
            {
            Log.Info("'Check for Updates' button clicked.");
            lblStatus.Text = "Checking for updates...";
            await RunUpdateCheck();
            }

        private async void btnUpdateAll_Click(object sender, EventArgs e)
            {
            Log.Info("'Update All' button clicked.");
            var productsToUpdate = _allProducts.Where(p => p.IsUpdateAvailable).ToList();
            if (!productsToUpdate.Any())
                {
                Log.Debug("No updates available to install.");
                MessageBox.Show("All modules are already up-to-date.", "Nothing to do", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
                }

            Log.Debug($"Found {productsToUpdate.Count} modules to update.");
            SetUiState(isChecking: true);
            lblStatus.Text = "Starting batch update...";

            // NEW: Prioritize configs if they are in the list
            foreach (var product in productsToUpdate.OrderBy(p => p.Id == AppConfig.DEVICE_CONFIGS_ID ? 0 : 1))
                {
                Log.Debug($"Batch updating: {product.Id}");

                // NEW: Route to correct handler
                if (product.Id == AppConfig.DEVICE_CONFIGS_ID)
                    {
                    lblStatus.Text = $"Updating {product.Name}...";
                    await RunConfigSyncTask(product, isReinstall: false);
                    }
                else
                    {
                    lblStatus.Text = $"Updating {product.Name}...";
                    await RunUpdateTask(product, isReinstall: product.IsRebuilt);
                    }
                }

            lblStatus.Text = "All updates complete.";
            Log.Info("Batch update complete.");
            SetUiState(isChecking: false);
            }

        private async void dgvModules_CellContentClick(object sender, DataGridViewCellEventArgs e)
            {
            if (e.RowIndex < 0 || _isUpdating) return;

            Product productClicked = dgvModules.Rows[e.RowIndex].DataBoundItem as Product;
            if (productClicked == null) return;

            // --- NEW: Route to config handler if clicked ---
            if (productClicked.Id == AppConfig.DEVICE_CONFIGS_ID)
                {
                await HandleConfigSyncClick(productClicked, e.ColumnIndex);
                return;
                }

            // --- Existing logic for regular modules ---
            Product productToUpdate = productClicked;
            bool isReinstall = false;

            if (e.ColumnIndex == dgvModules.Columns["UpdateAction"].Index)
                {
                if (!productToUpdate.IsUpdateAvailable)
                    {
                    Log.Debug("Clicked on '✓' (Up-to-date) button. Ignoring.");
                    return;
                    }
                Log.Info($"'Update/Install' button clicked for {productToUpdate.Id}");
                isReinstall = productToUpdate.IsRebuilt;
                }
            else if (e.ColumnIndex == dgvModules.Columns["ReinstallAction"].Index)
                {
                if (productToUpdate.InstalledVersion == "Not Installed")
                    {
                    Log.Debug("Clicked on inactive 'Re-install' icon. Ignoring.");
                    return; // Clicked on inactive icon
                    }

                Log.Info($"'Re-install' button clicked for {productToUpdate.Id}");
                var confirm = MessageBox.Show(
                    $"Are you sure you want to re-install '{productToUpdate.Name}'?\n" +
                    "This will delete all its local files and download the latest version.",
                    "Confirm Re-install",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm == DialogResult.No)
                    {
                    Log.Debug("User cancelled re-install.");
                    return;
                    }

                isReinstall = true;
                }
            else
                {
                return; // Clicked on a text cell
                }

            SetUiState(isChecking: true);
            await RunUpdateTask(productToUpdate, isReinstall);
            SetUiState(isChecking: false);
            }

        // --- NEW: Handler for the Device Config row ---
        private async Task HandleConfigSyncClick(Product configProduct, int columnIndex)
            {
            bool isReinstall = false;

            if (columnIndex == dgvModules.Columns["UpdateAction"].Index)
                {
                if (!configProduct.IsUpdateAvailable)
                    {
                    Log.Debug("Clicked on '✓' (Up-to-date) button for configs. Ignoring.");
                    return;
                    }
                Log.Info($"'Update/Install' button clicked for {configProduct.Id}");
                isReinstall = false;
                }
            else if (columnIndex == dgvModules.Columns["ReinstallAction"].Index)
                {
                string localConfigPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
                if (!Directory.Exists(localConfigPath) || !Directory.GetFiles(localConfigPath).Any())
                    {
                    Log.Debug("Clicked on inactive 'Re-install' icon for configs. Ignoring.");
                    return; // Clicked on inactive icon
                    }

                Log.Info($"'Re-install' button clicked for {configProduct.Id}");
                var confirm = MessageBox.Show(
                    $"Are you sure you want to re-install 'Device configs'?\n" +
                    "This will delete all local configs and re-download them from the server.",
                    "Confirm Re-install",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm == DialogResult.No)
                    {
                    Log.Debug("User cancelled config re-install.");
                    return;
                    }
                isReinstall = true;
                }
            else
                {
                return; // Clicked on a text cell
                }

            SetUiState(isChecking: true);
            await RunConfigSyncTask(configProduct, isReinstall);
            SetUiState(isChecking: false);
            }

        // --- NEW: Task runner for config sync ---
        private async Task RunConfigSyncTask(Product product, bool isReinstall)
            {
            string action = isReinstall ? "Re-installing" : "Updating";
            Log.Debug($"Starting task '{action}' for {product.Id}");
            lblStatus.Text = $"{action} {product.Name}...";
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;

            IProgress<string> statusProgress = new Progress<string>(status => lblStatus.Text = status);
            IProgress<int> percentProgress = new Progress<int>(percent => UpdateProgressBar(percent));

            bool success = false;

            try
                {
                if (isReinstall)
                    {
                    statusProgress.Report("Deleting old configs...");
                    _updateService.DeleteDeviceConfigs();
                    }
                await _updateService.SyncDeviceConfigsAsync(statusProgress, percentProgress);
                success = true;
                Log.Info($"Task '{action}' for {product.Id} successful.");
                }
            catch (Exception ex)
                {
                Log.Error(ex, $"Task '{action}' for {product.Id} failed.");
                MessageBox.Show($"Failed to {action.ToLower()} {product.Name}: {ex.Message}", "Task Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Re-check state on failure
                await RunUpdateCheck();
                }
            finally
                {
                if (success)
                    {
                    // Manually re-check the state to update IsUpdateAvailable
                    try
                        {
                        var serverFiles = await _nextcloudClient.ListFilesAsync(AppConfig.NC_DEVICE_CONFIGS_URL);
                        string localConfigPath = Path.Combine(Application.StartupPath, AppConfig.LOCAL_DEVICE_CONFIGS_PATH);
                        int localFileCount = Directory.Exists(localConfigPath) ? Directory.GetFiles(localConfigPath).Length : 0;
                        product.IsUpdateAvailable = !Directory.Exists(localConfigPath) || serverFiles.Count > localFileCount;
                        }
                    catch { /* Ignore errors here, UI will just be slightly off */ }
                    }

                // Refresh UI
                ( dgvModules.DataSource as BindingSource )?.ResetBindings(false);
                UpdateRowButtons();
                lblStatus.Text = success ? "Ready." : "Task failed.";
                progressBar.Value = 0;

                // Note: SetUiState(false) is called by the *caller* (dgvModules_CellContentClick or btnUpdateAll_Click)
                }
            }


        private async Task RunUpdateTask(Product product, bool isReinstall)
            {
            string action = isReinstall ? "Re-installing" : "Updating";
            Log.Debug($"Starting task '{action}' for {product.Id} v{product.LatestVersion}");
            lblStatus.Text = isReinstall ? $"Re-installing {product.Name}..." : $"Starting {product.Name}...";
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;

            IProgress<string> statusProgress = new Progress<string>(status => lblStatus.Text = status);
            IProgress<int> percentProgress = new Progress<int>(percent => UpdateProgressBar(percent));

            bool success = false;
            string manifestHash = null;

            try
                {
                var (manifest, hash) = await _nextcloudClient.FetchFileManifestAsync(product.Id, product.LatestVersion);
                manifestHash = hash;
                if (isReinstall)
                    {
                    await _updateService.ReinstallModuleAsync(product, statusProgress, percentProgress);
                    }
                else
                    {

                    if (manifest.IsApp)
                        {
                        Log.Debug("Task mode: APP (code + wheels)");
                        await _updateService.RunAppUpdateAsync(product, statusProgress, percentProgress);
                        }
                    else if (manifest.PackageMode == "zip")
                        {
                        Log.Debug("Task mode: ZIP");
                        await _updateService.StartModuleUpdate_Zip(manifest, statusProgress, percentProgress);
                        }
                    else
                        {
                        Log.Debug("Task mode: FILES (Delta)");
                        await _updateService.RunDeltaUpdateAsync(product, statusProgress, percentProgress);
                        }
                    }

                // If we got here, it worked.
                success = true;
                Log.Info($"Task '{action}' for {product.Id} successful.");
                }
            catch (OperationCanceledException ex)
                {
                Log.Warn($"Task '{action}' for {product.Id} cancelled by the user: {ex.Message}");
                await RunUpdateCheck(offerRebuilt: false);
                }
            catch (Exception ex)
                {
                Log.Error(ex, $"Task '{action}' for {product.Id} failed.");
                MessageBox.Show($"Failed to {action.ToLower()} {product.Name}: {ex.Message}", "Task Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Re-check state on failure
                await RunUpdateCheck(offerRebuilt: false);
                }
            finally
                {
                // --- NEW UI REFRESH LOGIC ---
                // This block now runs AFTER the try/catch is fully complete.

                if (success)
                    {
                    // 1. Update the data object
                    _localState.SaveLocalVersion(product.Id, product.LatestVersion, manifestHash);
                    product.InstalledVersion = product.LatestVersion;
                    product.IsUpdateAvailable = false;
                    product.IsRebuilt = false;
                    }

                // 2. Force the BindingSource to re-read the *entire* list
                ( dgvModules.DataSource as BindingSource )?.ResetBindings(false);

                // 3. Update button styles
                UpdateRowButtons();

                // 4. Set the final status
                lblStatus.Text = success ? "Ready." : "Task failed.";
                progressBar.Value = 0;

                // 5. Re-enable the UI
                // Note: SetUiState(false) is called by the *caller* (dgvModules_CellContentClick or btnUpdateAll_Click)
                }
            }

        #endregion

        #region Helper Methods (Stub, UI)

        private void UpdateProgressBar(int percent)
            {
            if (this.InvokeRequired)
                {
                this.Invoke((MethodInvoker)delegate
                    {
                        if (percent > progressBar.Maximum) percent = progressBar.Maximum;
                        if (percent < progressBar.Minimum) percent = progressBar.Minimum;
                        progressBar.Value = percent;
                        });
                }
            else
                {
                if (percent > progressBar.Maximum) percent = progressBar.Maximum;
                if (percent < progressBar.Minimum) percent = progressBar.Minimum;
                progressBar.Value = percent;
                }
            }

        #endregion
        }
    }