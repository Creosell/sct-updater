# Releasing Python modules of the Screen Check Suite

This is the single reference for how Python modules (Nextcloud CLI, Report Generator, ...) are packaged, released and
installed by the Updater. Every module repository links here from its own README and CLAUDE.md. **When anything about the
release process changes, update this file and the module instructions together.**

## Status

| Part | State |
|------|-------|
| Release tool `tools/release/app_release.py` (`app`, `upgrade`, `zip`, `gc`; the lock file is maintained automatically) | Implemented, 63 tests, verified by real releases of both modules |
| Wheel installer `WheelInstaller` (Updater) | Implemented, tested against real wheels |
| Updater support for `package_mode: app` (Python core, code delta, wheels, reinstall, migration from zip) | Implemented, 49 Updater tests; verified on the dev channel (2026-11-24): self-update 1.1.1 -> 1.2.0, installation of all modules through the Updater |
| Rust launcher (`launcher/`), built per module by `app_release.py` | Implemented, 4 unit tests, end-to-end checked with the real embeddable Python |
| Modules prepared for this scheme: Nextcloud CLI and Report Generator (`sct_app.toml`, `requirements.in/.lock`) | Installed through the Updater on the dev channel (CLI 1.3.0, Report Generator 1.6.0): reports are generated and uploaded |
| Legacy PyInstaller/zip release path (`release_manager.py`, specs, PyInstaller dependencies) | Removed from the Updater and module repositories (stage 7) |
| Cleanup of cached wheels (`apps/wheelhouse`) after an install, `gc` of unused wheels and Python archives on the server | Implemented, tested |

## Idea in one paragraph

Each module is independent and owns its dependencies. A release uploads the module code (delta by SHA256, like the `files`
mode) and the wheels listed in the module's lock file; wheels already on the server are not uploaded again, and clients
download only the wheels they do not have. The Python interpreter (embeddable distribution) is the only shared piece. The
Updater unpacks wheels into `apps/<module>` itself (no pip, no uv on clients), and a small Rust launcher with the old exe
name starts the module.

## Layout

```
Server  SCT/Updater/
  suite_manifest.json                      products / dev lists (versions are edited by hand)
  runtime/wheels/<name>.whl                all wheels of all modules, stored once
  runtime/python/python-<ver>-embed-amd64.zip, runtime/python_<minor>.json
  versions/<module>/<ver>/...              code files of one version
  versions/<module>/<module>_<ver>.json    file manifest (package_mode "app")

Client  <Updater folder>/
  apps/python_runtime/                     embeddable Python (shared)
  apps/<module>/                           module code + its wheels (own site-packages)
  apps/wheelhouse/                         local wheel cache (shared; the name is reserved, not a module id)
  config/                                  shared resources and device_configs (SCT_ROOT/config)
  <Module>.exe                             Rust launcher
```

## Files in a module repository

| File | Purpose |
|------|---------|
| `sct_app.toml` | What to ship: module `id`, optional `launcher` exe name with its `title` and `icon`, `[[files]]` (`src` -> `dst`, `exclude`, `target = "root"`) |
| `assets/app.ico` | Icon of the launcher (any path inside the repository, set with `icon`); a multi-size .ico (16, 24, 32, 48, 64, 256 px) |
| `.python-version` | Python version of the module (only the minor part, e.g. `3.13`, is used); uv reads the same file |
| `requirements.in` | Runtime dependencies only (never release tooling, PyInstaller, pytest) |
| `requirements.lock` | Exact versions with hashes, written by `app_release.py`: created or rewritten automatically (pinned versions are kept) whenever `requirements.in` changed; commit it |

`sct_app.toml` fields: `id`, optional `launcher`, `title` (shown as the file description of the launcher, default: the id), `icon` (path of a `.ico` file in the module repository), and a list of `[[files]]`. The dependency files have fixed names.
A `[[files]]` entry is installed under `apps/<id>/<dst>`; with `target = "root"` it is installed relative to the Updater
folder instead (used for shared resources such as `config/report_template.html`). `__pycache__` and `*.pyc` are never shipped.

## Commands

All commands run from the Updater repository. The tool needs `tools/release/.env` (copy `.env.example`) with the
Nextcloud credentials, and [uv](https://docs.astral.sh/uv/).

```bash
# Release a module version: code, missing wheels, Python (if the server lacks it), manifest last.
# The lock file is updated automatically when requirements.in changed (commit it).
# Without --upload it is a dry run: the manifest is prepared and nothing is sent.
uv run tools/release/app_release.py app D:\Python_scripts\Nextcloud_CLI 1.3.0 --upload

# Move the pinned dependency versions to the newest ones requirements.in allows
uv run tools/release/app_release.py upgrade D:\Python_scripts\Nextcloud_CLI

# List the wheels and Python archives on the server that no listed version uses; --delete removes them
uv run tools/release/app_release.py gc
```

* The wheel and Python cache is the folder in the `SCT_WHEELHOUSE` environment variable (default `~/.sct_wheelhouse`).
  Missing wheels are downloaded with pip; every wheel is verified against the hashes in the lock file.
* A dry run writes `release_artifacts/<id>_<version>.json` and uploads nothing.
* The embeddable Python is pinned in `runtime/python_embed.json` (version and SHA256) and must match the minor version in
  the module's `.python-version`; `app --upload` publishes it automatically when the server does not have it yet.
* After uploading, **edit the version in `suite_manifest.json` by hand**: put it into `dev` first, into `products`
  when it is verified.

## Typical tasks

**Release new module code (no dependency change):** `app <module> <version> --upload`. Only the changed code files are
uploaded; clients download only the changed files.

**Add a dependency:** edit `requirements.in` and run `app <module> <version>`: the lock is updated automatically (commit it); with `--upload` only the new wheels are uploaded and downloaded. Nothing in the Updater repository has to change.

**Upgrade dependency versions:** `upgrade <module>` moves every pin to the newest version `requirements.in` allows; commit the lock and release as usual.

**Upgrade Python:** change `runtime/python_embed.json` and every module's `.python-version`, then run `upgrade` and
`app --upload` for every module (wheels are built per Python minor version; the next `app --upload` publishes the new
Python). Patch upgrades (3.13.x) only need the new version and SHA256 in `runtime/python_embed.json`.

**Release the Updater itself:** `uv run tools/release/app_release.py app . --upload` from the Updater repository (its `sct_app.toml` has `kind = "dotnet"`): builds it with MSBuild, zips it and reads the version from the built `Updater.exe`; see README. Any other .NET project (Screen Checker) is released the same way from its own repository; a ready folder with `zip <folder> <product_id> <version> --upload`.
Roll the new Updater out through `products` **before** releasing modules in the `app` mode: older Updaters do not
understand that manifest.

## Rules and pitfalls

* One Python minor version for the whole suite (currently 3.13). A module declares it in `sct_app.toml`.
* A module must not rely on the location of its script. Shared resources are read from `SCT_ROOT/config`; the launcher sets
  `SCT_ROOT` to the Updater folder. The working directory is not changed (inherited from the caller). Report Generator
  resolves this in `src/paths.py` (priority: `SCT_ROOT`, frozen-exe folder, current directory).
* Never enable `import site` in `python313._pth`: the `urllib3_future` wheel ships a `.pth` that would overwrite `urllib3`.
* Use `AsyncNextcloud` in code (`nc-py-api` 0.31 removed the synchronous client).
* Wheels must exist for `win_amd64` and the module's Python minor version; the tool downloads binary wheels only.
* The Nextcloud server keeps stale locks on directories after an interrupted upload (`423 Locked` on delete). The wheel
  store is a flat folder (`runtime/wheels`) to keep this harmless; if a folder stays locked, an administrator must clear
  `oc_file_locks` (or the Redis lock keys).
* The launcher name is a contract with the programs that start the module: Screen Checker runs `Report Generator.exe` and `Nextcloud CLI.exe` by these names (spaces are allowed, like `Screen Checker.exe`). Renaming a launcher must be released together with the caller, and a module still delivered as a `zip` keeps its old name until it moves to the `app` package.
* Do not release a version that already exists in `products`: it would change what production clients installed.

## Testing

```bash
dotnet test SCT_Updater.Tests/SCT_Updater.Tests.csproj     # Updater (49 tests)
python -m pytest tools/release/tests                       # release tool (63 tests)
cd launcher && SCT_APP=demo cargo test                    # launcher (4 tests)
```

The Updater project builds with `dotnet build` (the .NET 8 SDK) for development and tests; the release build uses MSBuild (one path for every .NET project, see README), always into a clean folder, so there is no state of a test installation to protect.

## What the Updater does with an `app` package

Installing or updating a module (`UpdateService.RunAppUpdateAsync`):

1. **Python core.** Reads `runtime/python_<minor>.json`; if `apps/python_runtime/.sct_python.json` does not carry the same SHA256, downloads the archive, verifies it, unpacks it into `apps/python_runtime.new` and swaps the folder (the "Application in use" dialog handles locked files).
2. **Code.** Same delta as the `files` mode: only files whose hash changed are downloaded from `versions/<module>/<ver>/<path>`; files that disappeared from the new manifest are deleted. Shared files with `target = "root"` (for example `config/...`) are ordinary manifest paths.
3. **Wheels.** Compares the manifest wheel list with `apps/<module>/.sct_wheels.json`. If it differs: missing wheels are downloaded into `apps/wheelhouse` (SHA256 verified, a bad download is rejected before anything is unpacked), the files unpacked from the previous wheels are removed, and all wheels are unpacked into `apps/<module>` (which Python puts on `sys.path` when it runs the folder). The marker file records the wheels and the files they wrote.
4. **Legacy layout.** When a module moves from the `zip` package, its old top-level items are removed first (clean install of the new layout), and `lib_<module>` folders of the former PyInstaller layout are deleted afterwards.

Reinstall deletes `apps/<module>` and the module's files outside `apps`, never `apps/python_runtime`, other modules or the rest of `config/`; wheels are then unpacked again from the local cache without downloading.

**Wheel cache.** After every install the Updater deletes the wheels in `apps/wheelhouse` that no installed module lists in its `.sct_wheels.json` (replaced versions, interrupted downloads), so the cache holds only what a reinstall can need. A failure to clean is logged and ignored.

**Server cleanup (`gc`).** Wheels and Python archives pile up on the server. `app_release.py gc` keeps what the versions listed in `products` and `dev` of `suite_manifest.json` use (and the Python archives named by `runtime/python_<minor>.json`) and reports the rest; `--delete` removes it. It stops without deleting when a listed manifest cannot be read or no app version is listed. Re-releasing an old version uploads its missing wheels again, so nothing is lost for good.

## The launcher

`launcher/` holds one small Rust program (about 200 KB, no dependencies). `app_release.py app` builds it for every module whose `sct_app.toml` has `launcher = "<Name>.exe"` (the module id is baked in with `SCT_APP`) and ships it at the Updater root, so existing scripts that call the old exe name keep working. Requires the Rust toolchain (`cargo`).

What the launcher does when started: runs `apps/python_runtime/python.exe -X utf8 apps/<module> <arguments>`, passes the arguments unchanged (spaces and non-ASCII characters included), keeps the working directory of the caller, sets `SCT_ROOT` to the Updater folder, forwards stdout/stderr and returns the exit code of the module. It survives Ctrl+C, so the module (which gets it too) can finish and its exit code is still returned. If the runtime or the module is missing it prints how to repair it (run the Updater) and exits with 1.

**Name, icon and file properties.** `app_release.py` passes the exe name, `title` and `icon` to `launcher/build.rs`, which embeds them with the `winresource` crate (needs the Windows SDK resource compiler, found automatically). The result is built under its final name, for example `release_artifacts/launcher/nextcloud_cli/release/Nextcloud CLI.exe`, and shows in Explorer with the module icon and, under Details: description = `title`, product = "Screen Check Suite", original file name = the launcher name. The file and product version is the module release version passed to `app` (`1.3.0`), so Explorer shows what is installed; the version of the launcher itself (`launcher/Cargo.toml`) is kept in the Comments field (`Launcher 1.0.0`).

The build is reproducible (`/Brepro` in `launcher/.cargo/config.toml`, also from a different folder): the same module version always gives the same exe hash. The launcher is rebuilt and downloaded with every release (about 200 KB). Do not remove that file, and build the launcher only through `app_release.py` (cargo reads `.cargo/config.toml` from the working directory).

## Testing a module before production

Test the way clients get it: release the version, put it into the `dev` list of `suite_manifest.json`, and run an Updater with `NC_CHANNEL=dev` in its `.env` (see the dev channel in the README). The Updater then installs the module exactly as it will in production: Python, code, wheels and launcher. Re-releasing the same version with changed content is detected as a rebuilt package and offered as a reinstall, so a test cycle is: change, `app <module> <version> --upload`, check on the test machine. A dry run (`app` without `--upload`) shows the manifest the release would publish.

Nextcloud CLI and Report Generator were checked earlier in a local installation built by a since-removed helper: the CLI against the QA server and Report Generator byte-identical to its own virtual environment.
