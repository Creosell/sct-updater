# SCT Updater

Windows application for managing updates of Screen Check Tool (SCT) suite components. Downloads and installs module updates from Nextcloud.

## Features

- **Suite update management** — checks server manifest, compares installed versions against latest available
- **Self-update** — detects when the Updater itself needs updating, performs self-replacement via `update_stub.bat`
- **ZIP update pre-cleanup** — before extracting a new ZIP, fetches the old version manifest and removes stale files
- **Device configs sync** — detects outdated `config/device_configs/` by comparing local file count against server
- **Delta updates** — for file-mode packages, only downloads changed files based on hash comparison
- **Parallel downloads** — up to 20 simultaneous downloads via `HttpClient`
- **Local state tracking** — installed versions persisted in `local_versions.json`
- **Credentials via `.env`** — Nextcloud connection loaded from `.env` at startup (`NC_USER_QA`, `NC_PASSWORD_QA`, `NC_SERVER_URL_QA`)
- **Driver support** — drivers downloaded to `drivers/` folder; installation triggered manually via `install.bat`

## Requirements

- Windows 10/11
- .NET 8 Desktop Runtime (the same as Screen Checker needs)

## Configuration

Create `.env` based on `.env.example` and fill in your Nextcloud credentials:

```
NC_SERVER_URL_QA=https://your-nextcloud.example.com
NC_USER_QA=your_username
NC_PASSWORD_QA=your_app_password
```

## Releasing

The Updater and all Python modules are released with one tool, `tools/release/app_release.py`. Requires [uv](https://docs.astral.sh/uv/) with an active virtual environment that has the dependencies installed.

### Build specifics

The project is SDK-style and targets **.NET 8** (`net8.0-windows`, framework-dependent). Only the .NET 8 SDK is needed: `dotnet build SCT_Updater/SCT_Updater.csproj -c Release`. The release package is a single-file `Updater.exe` made by `dotnet publish` (`PublishSingleFile`), so there is no separate dll and no Costura.Fody.

### Release steps

**1. Configure credentials**

Create `tools/release/.env` based on `tools/release/.env.example` and fill in your Nextcloud credentials.

**2. Release**

From the repo root (without `--upload` it is a dry run: the Updater is built and packaged, nothing is sent):

```bash
uv run tools/release/app_release.py updater --upload
```

The tool publishes the Updater (`dotnet publish`, Release) into the clean folder `release_artifacts/build/suite_updater`, so the package is never stale and holds nothing but the fresh build. The Updater must stay a single exe; the tool refuses to package a build with loose dlls. The version is read from the built `Updater.exe` (`AssemblyVersion` / `FileVersion` in `SCT_Updater.csproj`), so it cannot differ from the build. The tool zips the folder, uploads the zip to `SCT/Updater/versions/suite_updater/<version>/` and publishes the manifest last. The files are also written to `release_artifacts/` (gitignored).

Any other build folder is released with the same tool: `uv run tools/release/app_release.py zip <folder> <product_id> <version> --upload` (a dry run without `--upload`).

**3. Roll it out**

Edit `suite_manifest.json` by hand: put the version into `dev` first, into `products` when it is verified. Self-update compares only the version number, so a changed build needs a new version.

### Python modules (Nextcloud CLI, Report Generator, ...)

Python modules are released with a separate tool, `tools/release/app_release.py` (commands `app` and `upgrade`). Each module repository contains only `sct_app.toml`, `requirements.in` and `requirements.lock`; the tool uploads the code and the missing wheels. See [docs/module-release.md](docs/module-release.md) for the full process, layout, rules and status.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
