# Release environment: quick start

How to prepare a machine so that every product of the suite (Updater, Nextcloud CLI, Report Generator, Screen Checker)
can be released with `tools/release/app_release.py`. The process itself (package layout, rules, what the Updater does)
is in [module-release.md](module-release.md); this page only gets the environment ready and shows the daily flow.

## What the tool needs

| Needed | For | Install |
|--------|-----|---------|
| Git | the four repositories | https://git-scm.com |
| [uv](https://docs.astral.sh/uv/) | runs the tool with its own dependencies; compiles `requirements.lock` | `winget install astral-sh.uv` |
| Visual Studio or Build Tools with the **.NET desktop build tools** workload (MSBuild) | Updater and Screen Checker (`kind = "dotnet"`; Screen Checker has a COM reference that `dotnet` cannot build) | Visual Studio Build Tools |
| .NET 8 SDK | `dotnet test` of the Updater (not used by the release build) | https://dotnet.microsoft.com |
| Rust (`rustup`, MSVC toolchain) and the Build Tools workload **Desktop development with C++** (linker and Windows SDK `rc.exe`) | the launcher exe of every Python module | https://rustup.rs |
| Internet access | the embeddable Python (python.org) and the wheels (PyPI) | - |
| A Nextcloud account with write access to `SCT/Updater` | upload | - |

A .NET-only release (the Updater, Screen Checker) does not need Rust; a Python module release does not need MSBuild.

## One-time setup

1. **Clone the repositories** to the paths of the `REPO_ALIASES` constants at the top of `tools/release/app_release.py`
   (or change the constants to your paths):

   | Alias | Default path |
   |-------|--------------|
   | `updater` | the repository that holds the tool itself |
   | `nextcloud-cli` | `D:\Python_scripts\sct-nextcloud-cli` |
   | `report-generator` | `D:\Python_scripts\sct-report-generator` |
   | `screen-checker` | `C:\Users\Pavel\source\repos\Creosell\sct-screen-checker` |

   An alias is not case sensitive, `_` equals `-`, and the `sct-` and `suite-` prefixes are optional, so `sct-updater`,
   `suite_updater` and `Report_Generator` work as well. A plain path is accepted instead of an alias.

2. **Credentials.** Create `tools/release/.env` (gitignored) with the Nextcloud account used for uploads:

   ```
   NC_URL=https://your-nextcloud.example.com
   NC_USER=your_username
   NC_PASS=your_app_password
   ```

   Use an app password, not the account password. Never commit this file.

3. **MSBuild.** Nothing to do when Visual Studio or Build Tools are installed: the tool finds MSBuild through the
   `MSBUILD` environment variable, then `PATH`, then `vswhere`. Set `MSBUILD` to the full path of `MSBuild.exe` only when
   it is installed in an unusual place.

4. **Wheel cache (optional).** Wheels and the embeddable Python are cached in `~/.sct_wheelhouse`. Set `SCT_WHEELHOUSE` to
   another folder to move it. The first Python release downloads everything; the next ones reuse the cache.

5. **The `app-release` command (optional).** Add a function to the PowerShell 7 profile (`notepad $PROFILE`) so the tool
   runs from any folder; `PATH` stays untouched:

   ```powershell
   function app-release { uv run --script C:\Users\Pavel\source\repos\sct-updater\tools\release\app_release.py @args }
   Set-Alias app_release app-release
   ```

   Open a new PowerShell window afterwards. Without it, run
   `uv run tools/release/app_release.py ...` from the Updater repository.

6. **Check the setup** with a dry run, which builds and packages everything and uploads nothing:

   ```powershell
   app-release app updater
   app-release app nextcloud-cli 1.1.0
   ```

   The packages and manifests appear in `release_artifacts/` of the Updater repository. A failure here is a missing tool
   from the table above (see Troubleshooting).

## Releasing a version

1. **Set the version** in the product and write its changelog entry:

   | Product | Where the version is |
   |---------|----------------------|
   | Updater | `Updater/Updater.csproj`: `AssemblyVersion` and `FileVersion`; the tool reads it from the built exe |
   | Screen Checker | `AssemblyVersion` in `Screen Checker.csproj`; read from the built exe |
   | Nextcloud CLI | the version argument of the command, and `APP_VERSION` in `nextcloud_cli.py` (keep them equal) |
   | Report Generator | the version argument, `APP_VERSION` in `main.py` and `version` in `pyproject.toml` (keep them equal) |

2. **Test** the product (`dotnet test Updater.Tests/Updater.Tests.csproj`, `python -m pytest tools/release/tests`, the
   module's own tests).
3. **Dry run** the release and read the summary (file count, wheels, Python, version):

   ```powershell
   app-release app report-generator 1.5.2
   ```

4. **Upload** with `--upload`. The manifest is published last, so a failed upload never exposes a half-published version:

   ```powershell
   app-release app updater --upload
   app-release app nextcloud-cli 1.1.0 --upload
   app-release app report-generator 1.5.2 --upload
   app-release app screen-checker --upload
   ```

   A .NET product takes no version argument (it is read from the exe); a Python module requires it. If a .NET version is
   given and differs from the built exe, the tool stops.
5. **Publish to the clients by hand** in `suite_manifest.json` on the server: first the `dev` list (test installation with
   `NC_CHANNEL=dev` in its `.env`), then `products`. Release the Updater first, then the modules and Screen Checker.
6. **Clean up** from time to time: `app-release gc` lists the wheels and Python archives that no listed version uses;
   `app-release gc --delete` removes them.

Re-uploading a version under the number that clients already have does not update the clients (versions are compared with
`!=`); the Updater only offers a reinstall for a changed manifest of the same version ("Rebuilt"). Use a new number for
anything clients must receive. A renamed exe must be listed in `obsolete` of the product's `sct_app.toml` in the same
release (see [module-release.md](module-release.md)).

## Commands

| Command | What it does |
|---------|--------------|
| `app <repo> [version] [--upload]` | releases a repository; dry run without `--upload` |
| `upgrade <repo>` | moves the pinned dependencies of a Python module to the newest versions `requirements.in` allows (commit the lock) |
| `zip <folder> <product_id> <version> [--upload]` | releases any ready build folder as a zip package |
| `gc [--delete]` | lists (and removes) unused wheels and Python archives on the server |

## Troubleshooting

| Message | Fix |
|---------|-----|
| `uv is required to compile the lock file.` | install uv and open a new terminal |
| `cargo is required to build the launcher` | install Rust from https://rustup.rs |
| `MSBuild not found: put it on PATH, set MSBUILD, ...` | install Build Tools with the .NET desktop build tools workload or set `MSBUILD` |
| linker or `rc.exe` error while building the launcher | install the **Desktop development with C++** workload (it brings the linker and the Windows SDK) |
| `repository folder not found: ... (aliases: ...)` | the alias is wrong or the repository is not at the path of `REPO_ALIASES` |
| `The version is required for a Python module.` | give the version: `app-release app report-generator 1.5.2` |
| `The built Updater.exe has version X, not Y` | change the version in the project, or drop the version argument |
| `The build must be a single exe, found: ...` | the project build produced loose dlls; keep `PublishSingleFile` |
| `KeyError: 'NC_URL'` or an authentication failure | fill `tools/release/.env` (see step 2) |
| `Upload failed. Manifest not published.` | nothing is exposed; run the same command again, only the missing files are sent |
| `obsolete lists files of the package` | remove that name from `obsolete` in `sct_app.toml` |
