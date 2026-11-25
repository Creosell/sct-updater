# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.2.0] - 2026-11-24

### Added
- "Application in use" dialog when a file cannot be written or deleted: close the application and continue, or cancel the update
- Without a valid `.env` the updater closes at startup and makes no server requests
- Update channels: `dev` list in `suite_manifest.json`, selected with `NC_CHANNEL=dev` in `.env` (title shows `[DEV]`); falls back to `products` when `dev` is missing or empty
- Rebuild detection: SHA256 of the installed file manifest is stored in `local_versions.json` (`manifest_hash`); if the server manifest of the same version differs, the product is flagged "Rebuilt" and a reinstall is offered
- `Updater.Tests` project (xUnit) covering channel resolution, rebuild detection, local state, wheel installation and app packages
- Rust launcher (`launcher/`) that starts a Python module from `apps/python_runtime` with `SCT_ROOT` set, built by `app_release.py` with the module icon and file properties (file version = module release version)
- `app_release.py app`: the Updater and every other .NET project are released with the same tool as the modules (`sct_app.toml` with `kind = "dotnet"`; built with MSBuild into a clean folder, single-exe check, version read from the built exe, manifest published last)
- The Updater deletes cached wheels (`apps/wheelhouse`) that no installed module uses after every install; `app_release.py gc` lists (`--delete` removes) wheels and Python archives on the server that no version in `products`/`dev` uses
- `app` package mode for Python modules: shared embeddable Python in `apps/python_runtime`, module code and wheels in `apps/<module>` (wheels verified by SHA256, cached in `apps/wheelhouse`), clean migration from `zip`; see `docs/module-release.md`
- `obsolete` list in `sct_app.toml` and in the manifest: the Updater deletes the listed old file names (renamed exes) from its root on install and update, for every package mode; replaces the hardcoded list for Screen Checker
- Diagnostics: `logs/sct_updater.log` is no longer erased at startup (the last runs are kept as `sct_updater.1.log` .. `.4.log`), every line carries the run id, the log starts with a header (Updater version, channel, Windows, runtime, locale and code pages, install folder), errors are logged with the full exception, unhandled exceptions are logged too
- The self-update restart script writes the robocopy log and exit code (`logs/self_update_robocopy.log`, `self_update_exit.txt`); the next run copies them into the main log. A self-update that did not replace the Updater is not repeated: the Updater shows an error instead of restarting in a loop

- `docs/release-setup.md`: quick start for a release machine (tools, credentials, aliases, release flow, troubleshooting)
- `app_release.py`: repository aliases instead of paths (`updater`, `nextcloud-cli`, `report-generator`, `screen-checker`); the README shows a PowerShell profile function `app-release` to run the tool from any folder

### Changed
- `Form1` renamed to `MainForm`, `Utility.cs.cs` to `Utility.cs`
- `description` removed from the suite manifest and the module grid
- Ported to .NET 8 (`net8.0-windows`, SDK-style project, framework-dependent single-file `Updater.exe`); Costura.Fody and `packages.config` removed, the version now lives in `Updater.csproj`
- New `app_release.py zip <folder> <product_id> <version>` releases any build folder

### Removed
- The Help menu with "Install drivers for CA-310" (the driver package is handed out manually; the Updater installs nothing from it)
- `tools/release/release_manager.py` (replaced by `app_release.py app` and `app_release.py zip`)
- `Updater/manifests/` and `Updater/release_artifacts/` (leftovers of the old release scripts, 1.0.0 packages)

### Fixed
- Self-update copied nothing: `Application.StartupPath` ends with a backslash on .NET 8, which escaped the closing quote of the robocopy command, so the Updater restarted in an endless loop with the old files
- The `logs` folder is created when missing, so a log is written in a clean installation folder

---

## [1.1.0] - 2026-08-11

### Added
- Device configs sync now supports nested folders (recursive listing via WebDAV `Depth: infinity`)
- Folders whose name starts with `_` (e.g. `_archive`) are skipped during config sync, at any nesting depth

---

## [1.0.1] - 2026-05-07

### Added
- Window title now displays application version (e.g. `SCT Updater 1.0.1`)

### Fixed
- ZIP update now removes old version files before extracting new archive (pre-cleanup via old manifest)
- Added `WPF LCD Test.exe` to legacy cleanup list for `screen_checker` product after rename

---

## [1.0.0] - 2026-01-23

Initial release.

### Features
- **Suite update management**: checks server manifest and compares installed component versions against latest available
- **Self-update**: detects when the Updater itself needs updating and performs self-replacement via `update_stub.bat`
- **Device configs sync**: detects missing or outdated `config/device_configs/` folder by comparing local file count against server
- **Parallel downloads**: up to 20 simultaneous file downloads via `HttpClient`
- **Local state tracking**: installed versions persisted in `local_versions.json`
- **Credentials via `.env`**: Nextcloud connection loaded from environment file at startup (`NC_USER_QA`, `NC_PASSWORD_QA`, `NC_SERVER_URL_QA`)
- **Driver support**: drivers downloaded to local `drivers/` folder; installation triggered manually via `install.bat`
