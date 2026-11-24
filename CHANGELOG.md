# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.1.1] - 2026-11-23

### Added
- `runtime-share` package mode: files are downloaded from the shared blob store `versions/python_runtime/<hash[:2]>/<hash>` (manifest `store_url`) and verified by SHA256 before being written; `release_manager.py runtime-share ...` uploads only the blobs missing from the store and publishes the product manifest last
- runtime-share installs compare every local file by SHA256 (unchanged files are skipped) and copy identical files from other installed products instead of downloading them; installed file hashes are stored in `local_versions.json` (`files`)
- Migration of a product from `zip` to `runtime-share` is a clean install of its top-level items
- "Application in use" dialog when a file cannot be written or deleted: close the application and continue, or cancel the update
- Without a valid `.env` the updater closes at startup and makes no server requests
- Update channels: `dev` list in `suite_manifest.json`, selected with `NC_CHANNEL=dev` in `.env` (title shows `[DEV]`); falls back to `products` when `dev` is missing or empty
- Rebuild detection: SHA256 of the installed file manifest is stored in `local_versions.json` (`manifest_hash`); if the server manifest of the same version differs, the product is flagged "Rebuilt" and a reinstall is offered
- `SCT_Updater.Tests` project (xUnit) covering channel resolution, rebuild detection, local state, runtime-share, wheel installation and app packages
- Rust launcher (`launcher/`) that starts a Python module from `apps/python_runtime` with `SCT_ROOT` set, built by `app_release.py`
- `app` package mode for Python modules: shared embeddable Python in `apps/python_runtime`, module code and wheels in `apps/<module>` (wheels verified by SHA256, cached in `tools/wheelhouse`), clean migration from `zip`; see `docs/module-release.md`

### Changed
- `description` removed from the suite manifest and the module grid

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
