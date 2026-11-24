# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Project Overview

WinForms desktop application (.NET 8, `net8.0-windows`) for managing updates of Screen Check Tool (SCT) suite components. Downloads and installs module updates from a Nextcloud server.

## Build & Run

**Build:** `dotnet build Updater/Updater.csproj -c Release` (the .NET 8 SDK is enough for development and tests).
Release package: built by `app_release.py app .` with **MSBuild** (publish, Release; Visual Studio or Build Tools, found through `MSBUILD`, `PATH` or vswhere): one framework-dependent single-file `Updater.exe` (about 2 MB; the .NET 8 Desktop Runtime must be installed, as for Screen Checker).

> **Requirement: the Updater is a single `Updater.exe`.** It is produced by `PublishSingleFile` (the project is SDK-style, so Costura.Fody is no longer used), and `app_release.py app .` refuses to package a build with loose dlls. Any change of the build must keep this.

## Architecture

**Pattern:** WinForms, single-form application

**Entry Point:** `Program.cs` → `MainForm`

**Key Classes:**

| Class | Purpose |
|-------|---------|
| `AppConfig` | Loads `.env`, exposes NC credentials and derived URLs |
| `NextcloudClient` | HTTP/WebDAV client for Nextcloud (download, PROPFIND, manifest fetch) |
| `UpdateService` | Core update logic: delta updates, ZIP updates, self-update, config sync |
| `LocalStateService` | Reads/writes `local_versions.json` |
| `MainForm` | UI: update check, product list, install/reinstall buttons |
| `Logger` | File log `logs/sct_updater.log` (rotated per run, run id on every line, header with system and locale) |
| `SelfUpdateGuard` | Stops a self-update loop and imports the restart script report (robocopy log and exit code) |

**Update flow:**
1. `CheckForUpdatesAsync` fetches `suite_manifest.json` from Nextcloud
2. Compares server versions against `local_versions.json`
3. Self-update (`UPDATER_ID = "suite_updater"`) takes priority — triggers before any other updates
4. ZIP mode: pre-cleanup via old manifest → download → extract → legacy file cleanup
5. Files mode: delta update — only changed files downloaded based on SHA256 hash

## Configuration

Runtime credentials in `Updater/.env` (gitignored):
```
NC_SERVER_URL_QA=https://your-nextcloud.example.com
NC_USER_QA=your_username
NC_PASSWORD_QA=your_app_password
```

## Version

Current version: **1.2.0** (set in `Updater\Updater.csproj` — `AssemblyVersion` / `FileVersion`). See [CHANGELOG.md](CHANGELOG.md) for history.

## Releasing

Environment setup for a release machine and the release flow: [docs/release-setup.md](docs/release-setup.md).

See [README.md](README.md#releasing) for full release instructions.

Release command (from repo root; `sct_app.toml` in the root describes the project, the tool builds it with MSBuild into a clean folder, reads the version from Updater.exe, and without `--upload` it is a dry run):
```bash
uv run tools/release/app_release.py app . --upload
```

The tool accepts repository aliases instead of paths (`updater`, `nextcloud-cli`, `report-generator`, `screen-checker`; see `REPO_ALIASES` in `app_release.py`); a PowerShell profile function `app-release` (see README) runs it from any folder: `app-release app report-generator 1.5.2 --upload`.

Any other build folder (for example Screen Checker) is released with `uv run tools/release/app_release.py zip <folder> <product_id> <version> --upload`.

## Python modules (Nextcloud CLI, Report Generator, ...)

The Updater, the other .NET projects and the Python modules are released with `tools/release/app_release.py` (`app`, `zip`, `upgrade`, `gc`); `app` looks at `sct_app.toml` of the repository: `kind = "dotnet"` builds a .NET project, otherwise it is a Python module. Each module repository holds `sct_app.toml`, `requirements.in` and `requirements.lock`; the tool uploads the code and only the wheels missing on the server (`runtime/wheels`). The Updater unpacks wheels into `apps/<module>` with `WheelInstaller`; the Python interpreter is the only shared piece.

Renamed exes: list the old file names in `obsolete = [...]` of `sct_app.toml`; the Updater deletes them from its root on install (see the guide).

The full process, file layout, rules (one Python minor version, `SCT_ROOT`, no `import site`, `AsyncNextcloud`) and implementation status are in [docs/module-release.md](docs/module-release.md). **Keep that file and the instructions of every module repository in sync when the release process changes.**

Tests: `dotnet test Updater.Tests/Updater.Tests.csproj` (Updater), `python -m pytest tools/release/tests` (release tool).

## Communication Style & Code Standards

**Response Format:**
1. **CONCISENESS**: Minimal responses. No pleasantries.
2. **FORMAT**: Use Markdown. Code blocks must specify language.
3. **SCOPE**: Return only changed code blocks with minimal context. DO NOT return full files unless explicitly requested.
4. **EXPLANATIONS**: Skip explanations for trivial changes.

**Code Style:**
- Prefer elegant, concise, and clean code
- Every method must have XML documentation in English
- No explanatory comments — only final descriptive comments

**Tone:**
- Communication in Russian, technical documentation strictly in English

## Project Map (graphify)

Knowledge graph of the codebase is stored in `graphify-out/` (`graph.json`, `graph.html`, `GRAPH_REPORT.md`). Use it to navigate before broad grep searches.

- Start with `graphify-out/GRAPH_REPORT.md` (community hubs, most connected nodes).
- CLI: `C:\Users\Pavel\.claude\tools\graphify\venv\Scripts\graphify.exe` (not in PATH):
  - `graphify explain "Symbol"` — node and its neighbors
  - `graphify path "A" "B"` — shortest path between two nodes
  - `graphify update .` — rebuild the graph after code changes (AST only, no LLM)
- Git hooks rebuild the graph after commit/checkout/merge/rewrite (`graphify hook status`).
- Freshness: compare `Built from commit` in `GRAPH_REPORT.md` with `git rev-parse HEAD`; run `graphify update .` if stale.
