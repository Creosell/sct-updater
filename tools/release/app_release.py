# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "loguru",
#     "nc-py-api",
#     "python-dotenv",
#     "packaging",
#     "pip",
# ]
# ///
"""Releases Python modules of the Screen Check Suite.

A module lives in its own repository and describes itself with ``sct_app.toml``; its Python minor version
comes from ``.python-version``, its dependencies from ``requirements.in`` and ``requirements.lock``. A release
uploads the module code, the wheels of its lock file (only those the server lacks), the embeddable Python
(only when the server lacks it) and a manifest. Modules are independent: each has its own dependencies.

Where a command takes a repository (app, upgrade) it accepts an alias from REPO_ALIASES instead of the path:
updater, nextcloud-cli, report-generator, screen-checker (case, "_" and the "sct-" prefix do not matter, so the
repository name and the product id work too); the paths are constants at the top of this file.
A PowerShell profile function runs the tool from any folder:
    function app-release { uv run --script <repo>\tools\release\app_release.py @args }
    app-release app report-generator 1.5.2 --upload

Commands (the wheel cache is the folder in the SCT_WHEELHOUSE environment variable, default ~/.sct_wheelhouse):
    app [--upload] [--promote prod|dev]
                       Release the project in a repository (dry run without --upload); --promote then sets the
                       released version as the current one of the channel (products or dev) in suite_manifest.json,
                       the product must already be listed there. The kind in sct_app.toml
                       decides: a Python module (default) uploads code, wheels and Python; kind = "dotnet" builds
                       the project with MSBuild (publish, Release) and releases it as a zip package, the version is
                       read from the built exe.
    upgrade            Move the pinned dependency versions to the newest ones requirements.in allows.
    zip [--upload]     Release any build folder as a zip package: zip <folder> <product_id> <version>.
    gc [--delete]      List (and with --delete remove) the wheels and Python archives on the server that no version in
                       suite_manifest.json (products and dev) uses; re-releasing an old version uploads its wheels again.

requirements.lock is maintained automatically: ``app`` rewrites it (keeping the pinned versions)
whenever requirements.in changed since the lock was made. Commit the lock when the tool says it was updated.

sct_app.toml of a .NET project (kind = "dotnet"):
    kind = "dotnet"
    id = "screen_checker"
    project = "WPF LCD Test/Screen Checker.csproj"
    exe = "Screen Checker.exe"          # the built exe; its file version is the release version
    publish_profile = "FolderProfile"   # optional: publish profile of the project (single-file settings)
    obsolete = ["WPF LCD Test.exe"]     # optional: files in the Updater root that an install deletes (renamed exes)

sct_app.toml (in the module repository):
    id = "report_generator"
    launcher = "Report Generator.exe"  # optional: Rust launcher built with cargo, installed at the Updater root
    title = "Report Generator"         # optional: shown as the file description of the launcher (default: id)
    icon = "assets/app.ico"            # optional: icon of the launcher, a .ico file in the module repository
    obsolete = ["ReportGenerator.exe"] # optional: files in the Updater root that an install deletes (renamed exes)
    [[files]]                          # source (file or folder) -> destination under apps/<id>/
    src = "main.py"
    dst = "__main__.py"
    [[files]]
    src = "src"
    dst = "src"
    exclude = ["tests"]                # optional, __pycache__ and *.pyc are always skipped
    [[files]]
    src = "config/report_template.html"
    dst = "config/report_template.html"
    target = "root"                    # optional: install relative to the Updater root, not apps/<id>/
"""
import argparse
import asyncio
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tomllib
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv
from loguru import logger
from nc_py_api import AsyncNextcloud, NextcloudException
from packaging.utils import canonicalize_name, parse_wheel_filename

REPO_ROOT = Path(__file__).resolve().parents[2]
RUNTIME_DIR = REPO_ROOT / "runtime"
PYTHON_EMBED_FILE = RUNTIME_DIR / "python_embed.json"
PACKAGE_MODE = "app"
BUILD_DIR = REPO_ROOT / "release_artifacts" / "build"
LAUNCHER_DIR = REPO_ROOT / "launcher"
REMOTE_ROOT = "SCT/Updater"
WHEELS_REMOTE = f"{REMOTE_ROOT}/runtime/wheels"
PYTHON_REMOTE = f"{REMOTE_ROOT}/runtime/python"
PYTHON_DESCRIPTOR_REMOTE = f"{REMOTE_ROOT}/runtime"
MAX_WORKERS = 8
UPLOAD_RETRIES = 3
SUITE_MANIFEST_REMOTE = f"{REMOTE_ROOT}/suite_manifest.json"
# --promote channel -> list of suite_manifest.json that holds the current versions of the channel
CHANNEL_KEYS = {"prod": "products", "dev": "dev"}
PROMOTE_HELP = "after --upload, set the released version as the current one of the channel in suite_manifest.json"

UPDATER_REPO = REPO_ROOT
NEXTCLOUD_CLI_REPO = Path(r"D:\Python_scripts\sct-nextcloud-cli")
REPORT_GENERATOR_REPO = Path(r"D:\Python_scripts\sct-report-generator")
SCREEN_CHECKER_REPO = Path(r"C:\Users\Pavel\source\repos\Creosell\sct-screen-checker")
# Names accepted instead of a repository path in the commands; see alias_key for the spelling that is accepted
REPO_ALIASES = {
    "updater": UPDATER_REPO,
    "nextcloud-cli": NEXTCLOUD_CLI_REPO,
    "report-generator": REPORT_GENERATOR_REPO,
    "screen-checker": SCREEN_CHECKER_REPO,
}

load_dotenv(Path(__file__).parent / ".env")
logger.remove()
logger.add(sys.stderr, format="<green>{time:HH:mm:ss}</green> | {message}", level="INFO")


@dataclass
class Pin:
    """One locked package: its exact version and the hashes of all accepted files."""
    name: str
    version: str
    hashes: set[str] = field(default_factory=set)


@dataclass
class AppSpec:
    """Release descriptor of a module, read from sct_app.toml."""
    root: Path
    id: str
    python: str
    requirements: Path
    lock: Path
    files: list[dict]
    launcher: str | None = None
    title: str | None = None
    icon: Path | None = None
    obsolete: list[str] = field(default_factory=list)


@dataclass
class DotnetSpec:
    """Release descriptor of a .NET project, read from sct_app.toml (kind = "dotnet")."""
    root: Path
    id: str
    project: Path
    exe: str
    publish_profile: str | None = None
    obsolete: list[str] = field(default_factory=list)


def read_descriptor(repo_dir: Path) -> dict:
    """Reads the sct_app.toml of a repository."""
    path = repo_dir / "sct_app.toml"
    if not path.exists():
        raise FileNotFoundError(f"{path} not found; add the descriptor to the repository.")
    return tomllib.loads(path.read_text(encoding="utf-8"))


def read_obsolete(data: dict) -> list[str]:
    """Returns the 'obsolete' list of sct_app.toml: file names in the Updater root that an install deletes (renamed exes)."""
    names = data.get("obsolete", [])
    if not isinstance(names, list) or not all(isinstance(n, str) and re.fullmatch(r"[^\/:*?\"<>|]+", n) and n not in (".", "..") for n in names):
        raise ValueError("sct_app.toml: obsolete must be a list of plain file names")
    return names


def load_dotnet_spec(repo_dir: Path) -> DotnetSpec:
    """Validates the sct_app.toml of a .NET project repository."""
    data = read_descriptor(repo_dir)
    for key in ("id", "project", "exe"):
        if key not in data:
            raise ValueError(f"sct_app.toml: '{key}' is required for kind = \"dotnet\"")
    project = repo_dir / data["project"]
    if not project.is_file():
        raise FileNotFoundError(f"sct_app.toml: project not found: {project}")
    return DotnetSpec(repo_dir, data["id"], project, data["exe"], data.get("publish_profile"), read_obsolete(data))


def sha256_of(path: Path) -> str:
    """Returns the SHA256 of a file as lowercase hex."""
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_spec(module_dir: Path) -> AppSpec:
    """Reads and validates the sct_app.toml of a module repository."""
    data = read_descriptor(module_dir)
    path = module_dir / "sct_app.toml"
    for key in ("id", "files"):
        if key not in data:
            raise ValueError(f"{path.name}: '{key}' is required")
    launcher = data.get("launcher")
    if launcher is not None and not re.fullmatch(r"[^\\/:*?\"<>|]+\.exe", launcher):
        raise ValueError(f"{path.name}: launcher must be a file name ending with .exe")
    icon = None
    if "icon" in data:
        icon = module_dir / data["icon"]
        if icon.suffix.lower() != ".ico" or not icon.is_file():
            raise FileNotFoundError(f"{path.name}: icon must be an existing .ico file, got {icon}")
    return AppSpec(module_dir, data["id"], read_python_minor(module_dir),
                   module_dir / "requirements.in", module_dir / "requirements.lock", data["files"],
                   launcher, data.get("title"), icon, read_obsolete(data))


def read_python_minor(module_dir: Path) -> str:
    """Returns the Python minor version (e.g. "3.13") from the module's .python-version file."""
    path = module_dir / ".python-version"
    if not path.exists():
        raise FileNotFoundError(f"{path} not found; the module must pin its Python version there.")
    match = re.match(r"\s*(\d+\.\d+)", path.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"{path.name} must start with a Python version like 3.13")
    return match.group(1)


def build_launcher(spec: AppSpec, version: str) -> Path:
    """Builds the Rust launcher of a module with cargo and returns the path of the exe under its final name.

    The module id, the exe name, the title (file description), the module release version (file and product
    version) and the icon are baked into the exe through environment variables read by launcher/build.rs. The build runs inside the launcher folder so that its
    .cargo/config.toml (reproducible linking) applies.
    """
    cargo = shutil.which("cargo")
    if not cargo:
        sys.exit("cargo is required to build the launcher (install Rust from https://rustup.rs).")
    target = REPO_ROOT / "release_artifacts" / "launcher" / spec.id
    env = {**os.environ, "SCT_APP": spec.id, "SCT_EXE": spec.launcher, "SCT_TITLE": spec.title or spec.id,
           "SCT_VERSION": version}
    if spec.icon:
        env["SCT_ICON"] = str(spec.icon.resolve())
    subprocess.run([cargo, "build", "--release", "--target-dir", str(target)], cwd=LAUNCHER_DIR, env=env, check=True)

    built = target / "release" / spec.launcher
    os.replace(target / "release" / "sct_launcher.exe", built)
    return built


def with_launcher(spec: AppSpec, files: list[tuple[Path, str]], version: str,
                  build=build_launcher) -> list[tuple[Path, str]]:
    """Adds the module launcher, installed at the Updater root under its exe name, to the file list."""
    if not spec.launcher:
        return files
    return sorted([*files, (build(spec, version), spec.launcher)], key=lambda item: item[1])


def parse_lock(text: str) -> list[Pin]:
    """Parses a uv/pip-compile lock file with hashes into pins."""
    pins: list[Pin] = []
    for line in text.splitlines():
        stripped = line.strip().rstrip("\\").strip()
        header = re.match(r"^([A-Za-z0-9][A-Za-z0-9._-]*)==([^\s;]+)", stripped)
        if header and not line.startswith((" ", "\t")):
            pins.append(Pin(canonicalize_name(header.group(1)), header.group(2)))
        for digest in re.findall(r"--hash=sha256:([0-9a-f]{64})", stripped):
            if not pins:
                raise ValueError("Lock file has a hash before any package.")
            pins[-1].hashes.add(digest)
    if not pins:
        raise ValueError("Lock file has no pinned packages.")
    missing = [p.name for p in pins if not p.hashes]
    if missing:
        raise ValueError(f"Lock file lacks hashes for: {', '.join(missing)} (compile with --generate-hashes).")
    return pins


def collect_files(spec: AppSpec) -> list[tuple[Path, str]]:
    """Expands the descriptor into (source file, install path relative to the Updater root) pairs."""
    result: dict[str, Path] = {}
    for entry in spec.files:
        src = spec.root / entry["src"]
        dst = entry.get("dst", entry["src"]).replace("\\", "/").strip("/")
        base = dst if entry.get("target") == "root" else f"apps/{spec.id}/{dst}"
        skipped = set(entry.get("exclude", []))
        if src.is_file():
            result[base] = src
        elif src.is_dir():
            for path in sorted(src.rglob("*")):
                parts = path.relative_to(src).parts
                if not path.is_file() or "__pycache__" in parts or path.suffix == ".pyc" or skipped & set(parts):
                    continue
                result[f"{base}/{'/'.join(parts)}"] = path
        else:
            raise FileNotFoundError(f"{entry['src']} from sct_app.toml does not exist in {spec.root}")
    if not result:
        raise ValueError("sct_app.toml selects no files.")
    return [(path, rel) for rel, path in sorted(result.items())]


def find_wheel(wheelhouse: Path, pin: Pin) -> Path | None:
    """Finds a cached wheel of the pinned version whose SHA256 the lock accepts."""
    for wheel in wheelhouse.glob("*.whl"):
        try:
            name, version, _, _ = parse_wheel_filename(wheel.name)
        except Exception:
            continue
        if canonicalize_name(name) == pin.name and str(version) == pin.version and sha256_of(wheel) in pin.hashes:
            return wheel
    return None


def fill_wheelhouse(pins: list[Pin], wheelhouse: Path, python: str) -> list[Path]:
    """Makes sure every pin has a verified wheel in the local cache and returns the wheels in lock order."""
    wheelhouse.mkdir(parents=True, exist_ok=True)
    missing = [p for p in pins if find_wheel(wheelhouse, p) is None]
    if missing:
        logger.info(f"Downloading {len(missing)} wheel(s) into {wheelhouse}")
        requirements = wheelhouse / "_download.txt"
        requirements.write_text("".join(
            f"{p.name}=={p.version} " + " ".join(f"--hash=sha256:{h}" for h in sorted(p.hashes)) + "\n"
            for p in missing), encoding="utf-8")
        try:
            subprocess.run([sys.executable, "-m", "pip", "download", "-q", "-r", str(requirements),
                            "--require-hashes", "--no-deps", "--only-binary=:all:", "--platform", "win_amd64",
                            "--python-version", python, "--implementation", "cp", "-d", str(wheelhouse)],
                           check=True)
        finally:
            requirements.unlink(missing_ok=True)

    wheels = []
    for pin in pins:
        wheel = find_wheel(wheelhouse, pin)
        if wheel is None:
            raise RuntimeError(f"No wheel with an accepted hash for {pin.name}=={pin.version}")
        wheels.append(wheel)
    return wheels


def build_manifest(spec: AppSpec, version: str, files: list[tuple[Path, str]], wheels: list[Path]) -> dict:
    """Builds the file manifest of a module version in the 'app' package mode."""
    manifest = {
        "product_id": spec.id,
        "version": version,
        "base_url": f"versions/{spec.id}/{version}",
        "package_mode": PACKAGE_MODE,
        "python": spec.python,
        "wheels_url": "runtime/wheels",
        "files": [{"path": rel, "hash": sha256_of(path)} for path, rel in files],
        "wheels": [{"file": w.name, "sha256": sha256_of(w), "size": w.stat().st_size} for w in wheels],
    }
    own = {rel.lower() for _, rel in files}
    if clash := [n for n in spec.obsolete if n.lower() in own]:
        raise ValueError(f"sct_app.toml: obsolete lists files of the package: {', '.join(clash)}")
    if spec.obsolete:
        manifest["obsolete"] = spec.obsolete
    return manifest


def wheelhouse_dir() -> Path:
    """Returns the local wheel and Python cache: the SCT_WHEELHOUSE environment variable or ~/.sct_wheelhouse."""
    return Path(os.environ.get("SCT_WHEELHOUSE") or Path.home() / ".sct_wheelhouse")


def python_embed(wheelhouse: Path, minor: str) -> tuple[dict, Path]:
    """Returns the pinned embeddable Python descriptor and its cached zip, downloading it when needed.

    The pinned version must belong to the Python minor version the module declares.
    """
    info = json.loads(PYTHON_EMBED_FILE.read_text(encoding="utf-8"))
    version = info["version"]
    if not version.startswith(f"{minor}."):
        raise ValueError(f"The module uses Python {minor}, but {PYTHON_EMBED_FILE.name} pins {version}.")
    archive = wheelhouse / "python" / f"python-{version}-embed-amd64.zip"
    if not archive.exists():
        archive.parent.mkdir(parents=True, exist_ok=True)
        url = f"https://www.python.org/ftp/python/{version}/python-{version}-embed-amd64.zip"
        logger.info(f"Downloading {url}")
        urllib.request.urlretrieve(url, archive)

    actual = sha256_of(archive)
    if not info.get("sha256"):
        info["sha256"] = actual
        PYTHON_EMBED_FILE.write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
        logger.warning(f"Pinned SHA256 of the Python archive in {PYTHON_EMBED_FILE.name}; commit it.")
    elif actual != info["sha256"]:
        raise RuntimeError(f"Python archive hash mismatch: expected {info['sha256']}, got {actual}")
    return info, archive


# --- Nextcloud ---

async def get_nc_client() -> AsyncNextcloud:
    """Creates an authenticated async Nextcloud client; exits the process on auth failure."""
    nc = AsyncNextcloud(nextcloud_url=os.environ["NC_URL"], nc_auth_user=os.environ["NC_USER"],
                        nc_auth_pass=os.environ["NC_PASS"])
    try:
        await nc.users.get_user(os.environ["NC_USER"])
        return nc
    except Exception:
        logger.critical("Nextcloud auth failed")
        sys.exit(1)


async def ensure_remote_dir(nc, remote_dir: str, created: set[str]) -> None:
    """Creates a remote directory and its parents once."""
    current = ""
    for part in remote_dir.strip("/").split("/"):
        current = f"{current}/{part}".lstrip("/")
        if current in created:
            continue
        try:
            await nc.files.mkdir(current)
        except NextcloudException as e:
            if e.status_code not in (405, 409, 423):
                raise
        created.add(current)


async def list_remote(nc, remote_dir: str) -> dict[str, int]:
    """Lists the files of a remote directory as {name: size}; an absent directory is empty."""
    try:
        return {n.name: n.info.content_length for n in await nc.files.listdir(remote_dir) if not n.is_dir}
    except NextcloudException as e:
        if e.status_code == 404:
            return {}
        raise


async def upload_file(nc, local: Path, remote: str, created: set[str]) -> bool:
    """Uploads one file with retries; returns whether it succeeded."""
    for attempt in range(1, UPLOAD_RETRIES + 1):
        try:
            await ensure_remote_dir(nc, remote.rsplit("/", 1)[0], created)
            await nc.files.upload_stream(remote, local)
            return True
        except Exception as e:
            if attempt == UPLOAD_RETRIES:
                logger.error(f"Upload failed [{local.name}]: {e}")
                return False
            await asyncio.sleep(attempt)
    return False


async def upload_all(nc, jobs: list[tuple[Path, str]]) -> bool:
    """Uploads the given (local, remote) pairs with bounded concurrency; returns whether all succeeded."""
    created: set[str] = set()
    gate = asyncio.Semaphore(MAX_WORKERS)

    async def worker(local: Path, remote: str) -> bool:
        async with gate:
            return await upload_file(nc, local, remote, created)

    return all(await asyncio.gather(*(worker(local, remote) for local, remote in jobs)))


def python_descriptor(info: dict, archive: Path, folder: Path) -> Path:
    """Writes the descriptor the Updater reads for a Python minor version and returns its path."""
    minor = ".".join(info["version"].split(".")[:2])
    descriptor = folder / f"python_{minor}.json"
    descriptor.write_text(json.dumps({"version": info["version"], "file": archive.name, "sha256": info["sha256"],
                                      "size": archive.stat().st_size}, indent=2) + "\n", encoding="utf-8")
    return descriptor


async def read_remote_json(nc, remote: str) -> dict:
    """Downloads a JSON file from the server."""
    return json.loads((await nc.files.download(remote)).decode("utf-8"))


async def read_promoted_suite(nc, product_id: str, version: str, channel: str) -> dict:
    """Downloads suite_manifest.json and returns it with the version of the product set for the channel.

    Exits when the product is not listed in the channel, so a bad promotion is found before anything is uploaded.
    """
    suite = await read_remote_json(nc, SUITE_MANIFEST_REMOTE)
    key = CHANNEL_KEYS[channel]
    entry = next((p for p in suite.get(key, []) if p["id"] == product_id), None)
    if entry is None:
        sys.exit(f"{product_id} is not listed in '{key}' of suite_manifest.json; add the product there first.")
    entry["latest_version"] = version
    return suite


async def publish_suite(nc, suite: dict, out_dir: Path) -> None:
    """Uploads suite_manifest.json, the file the clients read to learn the current versions."""
    local = out_dir / "suite_manifest.json"
    local.write_text(json.dumps(suite, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    if not await upload_all(nc, [(local, SUITE_MANIFEST_REMOTE)]):
        sys.exit("suite_manifest.json upload failed")


async def upload_app(nc, manifest: dict, files: list[tuple[Path, str]], wheels: list[Path], manifest_path: Path,
                     python_zip: Path, python_json: Path, promote: str | None = None) -> None:
    """Uploads the missing wheels and Python archive and the code files, then publishes the manifest last.

    With ``promote`` ("prod" or "dev") the version is then set as the current one of that channel in suite_manifest.json.
    """
    suite = await read_promoted_suite(nc, manifest["product_id"], manifest["version"], promote) if promote else None
    remote_wheels = await list_remote(nc, WHEELS_REMOTE)
    remote_python = await list_remote(nc, PYTHON_REMOTE)
    missing = [w for w in wheels if remote_wheels.get(w.name) != w.stat().st_size]
    base = f"{REMOTE_ROOT}/{manifest['base_url']}"

    jobs = [(path, f"{base}/{rel}") for path, rel in files] + [(w, f"{WHEELS_REMOTE}/{w.name}") for w in missing]
    python_missing = remote_python.get(python_zip.name) != python_zip.stat().st_size
    if python_missing:
        jobs.append((python_zip, f"{PYTHON_REMOTE}/{python_zip.name}"))
    jobs.append((python_json, f"{PYTHON_DESCRIPTOR_REMOTE}/{python_json.name}"))
    size_mb = sum(local.stat().st_size for local, _ in jobs) / 1048576
    logger.info(f"{manifest['product_id']} {manifest['version']}: {len(files)} code files, {len(wheels)} wheels "
                f"({len(missing)} to upload), Python {'to upload' if python_missing else 'already on the server'} "
                f"| total upload {size_mb:.1f} MB")

    if not await upload_all(nc, jobs):
        logger.error("Some uploads failed. Manifest not published.")
        sys.exit(1)
    target = f"{REMOTE_ROOT}/versions/{manifest['product_id']}/{manifest_path.name}"
    if not await upload_all(nc, [(manifest_path, target)]):
        sys.exit("Manifest upload failed")
    if suite:
        await publish_suite(nc, suite, manifest_path.parent)
        logger.info(f"suite_manifest.json: {manifest['product_id']} {manifest['version']} is now current in {promote}")
    logger.success("Done!")


# --- Commands ---

LOCK_SOURCE_MARK = "# requirements.in sha256: "


def lock_source_hash(lock_text: str) -> str | None:
    """Returns the SHA256 of requirements.in recorded in a lock file, or None when there is no record."""
    for line in lock_text.splitlines():
        if line.startswith(LOCK_SOURCE_MARK):
            return line[len(LOCK_SOURCE_MARK):].strip()
    return None


def compile_lock(spec: AppSpec, upgrade: bool = False, run=subprocess.run) -> None:
    """Resolves requirements.in into requirements.lock with hashes and records which requirements.in it came from.

    Versions already pinned in the lock are kept unless ``upgrade`` is set.
    """
    uv = shutil.which("uv")
    if not uv:
        sys.exit("uv is required to compile the lock file.")
    command = [uv, "pip", "compile", str(spec.requirements), "--python-version", spec.python,
               "--python-platform", "windows", "--only-binary", ":all:", "--generate-hashes", "--no-header",
               "-o", str(spec.lock)]
    if upgrade:
        command.append("--upgrade")
    run(command, check=True)
    header = ("# Generated by tools/release/app_release.py; do not edit.\n"
              f"{LOCK_SOURCE_MARK}{sha256_of(spec.requirements)}\n")
    spec.lock.write_text(header + spec.lock.read_text(encoding="utf-8"), encoding="utf-8")


def ensure_lock(spec: AppSpec, compile_fn=compile_lock) -> bool:
    """Brings requirements.lock in line with requirements.in; returns whether the lock was rewritten.

    The lock is rewritten when it is missing or was made from a different requirements.in. Pinned versions are kept.
    """
    if not spec.requirements.exists():
        raise FileNotFoundError(f"{spec.requirements} not found; list the runtime dependencies of the module there.")
    if spec.lock.exists() and lock_source_hash(spec.lock.read_text(encoding="utf-8")) == sha256_of(spec.requirements):
        return False
    compile_fn(spec)
    logger.warning(f"{spec.lock.name} was updated from {spec.requirements.name}; commit it.")
    return True


def alias_key(value: str) -> str:
    """Normalizes an alias: any case, "_" like "-", the "sct-" and "suite-" prefixes optional, so that "updater",
    "sct-updater", "suite_updater" and "Report_Generator" all find their repository."""
    key = value.lower().replace("_", "-")
    for prefix in ("sct-", "suite-"):
        key = key.removeprefix(prefix)
    return key


def resolve_repo(value: str) -> Path:
    """Turns a repository alias (see REPO_ALIASES) or a path into an existing folder; used as an argparse type."""
    path = REPO_ALIASES.get(alias_key(value), Path(value))
    if not path.is_dir():
        raise argparse.ArgumentTypeError(f"repository folder not found: {path} (aliases: {', '.join(REPO_ALIASES)})")
    return path


def cmd_upgrade(args: argparse.Namespace) -> None:
    """Moves the pinned versions of a module to the newest ones allowed by requirements.in."""
    spec = load_spec(Path(args.module))
    compile_lock(spec, upgrade=True)
    logger.success(f"Versions upgraded in {spec.lock}; commit it.")


def read_module_versions(spec: AppSpec) -> dict[str, str]:
    """Returns the versions a module keeps: ``pyproject.toml`` (project.version) and every ``APP_VERSION = "x"`` of its .py files listed in sct_app.toml."""
    found: dict[str, str] = {}
    pyproject = spec.root / "pyproject.toml"
    if pyproject.is_file():
        version = tomllib.loads(pyproject.read_text(encoding="utf-8")).get("project", {}).get("version")
        if version:
            found[pyproject.name] = str(version)
    for entry in spec.files:
        source = spec.root / entry["src"]
        if source.suffix == ".py" and source.is_file():
            match = re.search(r"^APP_VERSION\s*=\s*[\"']([^\"']+)[\"']", source.read_text(encoding="utf-8"), re.MULTILINE)
            if match:
                found[source.name] = match.group(1)
    return found


def resolve_module_version(spec: AppSpec, requested: str | None) -> str:
    """Returns the release version: the one given, else the version the module keeps in its files (they must agree)."""
    found = read_module_versions(spec)
    if requested:
        stale = {name: v for name, v in found.items() if v != requested}
        if stale:
            logger.warning(f"Releasing {requested}, but the module still says " + ", ".join(f"{n} {v}" for n, v in stale.items()))
        return requested
    if not found:
        sys.exit("The version is required: the module has no pyproject.toml version and no APP_VERSION.")
    if len(set(found.values())) > 1:
        sys.exit("The version places disagree: " + ", ".join(f"{n} {v}" for n, v in found.items()) + ". Fix them or pass the version.")
    version = next(iter(found.values()))
    logger.info(f"Version {version} (from {', '.join(found)})")
    return version


def cmd_app(args: argparse.Namespace) -> None:
    """Prepares a release of the repository; uploads it only with --upload (otherwise a dry run)."""
    if read_descriptor(Path(args.module)).get("kind") == "dotnet":
        return cmd_dotnet(args)
    spec = load_spec(Path(args.module))
    args.version = resolve_module_version(spec, args.version)
    ensure_lock(spec)

    wheelhouse = wheelhouse_dir()
    files = with_launcher(spec, collect_files(spec), args.version)
    wheels = fill_wheelhouse(parse_lock(spec.lock.read_text(encoding="utf-8")), wheelhouse, spec.python)
    python, python_zip = python_embed(wheelhouse, spec.python)
    manifest = build_manifest(spec, args.version, files, wheels)

    out_dir = REPO_ROOT / "release_artifacts"
    out_dir.mkdir(exist_ok=True)
    manifest_path = out_dir / f"{spec.id}_{args.version}.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    python_json = python_descriptor(python, python_zip, out_dir)
    logger.info(f"Manifest: {manifest_path.name} | {len(files)} code files | {len(wheels)} wheels | Python {python['version']}")

    if not args.upload:
        logger.info("Dry run: nothing was uploaded. Add --upload to publish.")
        return

    async def run() -> None:
        await upload_app(await get_nc_client(), manifest, files, wheels, manifest_path, python_zip, python_json,
                         args.promote)

    asyncio.run(run())


def find_msbuild() -> Path:
    """Returns the MSBuild to use: the MSBUILD environment variable, then PATH, then the newest Visual Studio or
    Build Tools found with vswhere; exits when there is none."""
    configured = os.environ.get("MSBUILD") or shutil.which("msbuild")
    if configured:
        return Path(configured)
    vswhere = Path(os.environ.get("ProgramFiles(x86)", "")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if vswhere.is_file():
        found = subprocess.run([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild",
                                "-find", r"MSBuild\**\Bin\MSBuild.exe"], capture_output=True, text=True).stdout.strip()
        if found:
            return Path(found.splitlines()[0])
    sys.exit("MSBuild not found: put it on PATH, set MSBUILD, or install Visual Studio Build Tools "
             "(workload '.NET desktop build tools').")


def publish_dotnet(spec: DotnetSpec, run=subprocess.run) -> Path:
    """Publishes a .NET project (Release) with MSBuild into a clean folder and returns the folder.

    MSBuild is used for every project (one build path; COM references are not supported by ``dotnet``) and only
    its errors are shown, so the warnings of the project do not bury the release log. The folder is recreated on every run, so nothing but the fresh build can end up in the package. The result must be a
    single-file build: loose dlls stop the release.
    """
    out = BUILD_DIR / spec.id
    shutil.rmtree(out, ignore_errors=True)
    command = [str(find_msbuild()), str(spec.project), "-restore", "-t:Publish", "-p:Configuration=Release",
               f"-p:PublishDir={out}{os.sep}", "-clp:ErrorsOnly", "-nologo"]
    if spec.publish_profile:
        command.append(f"-p:PublishProfile={spec.publish_profile}")
    run(command, check=True)
    loose = sorted(item.name for item in out.rglob("*.dll"))
    if loose:
        sys.exit(f"The build must be a single exe, found: {', '.join(loose)}")
    return out


async def referenced_runtime_files(nc) -> tuple[set[str], set[str]]:
    """Returns the wheel names and the Python archive names used by the versions the clients can install.

    Those are the "app" versions listed in the products and dev lists of suite_manifest.json and the archives named
    by the runtime/python_<minor>.json descriptors. Exits when a listed manifest cannot be read, so nothing is
    judged unused on incomplete information.
    """
    suite = await read_remote_json(nc, SUITE_MANIFEST_REMOTE)
    listed = sorted({(p["id"], p["latest_version"]) for key in ("products", "dev") for p in suite.get(key, [])})
    wheels: set[str] = set()
    for product_id, version in listed:
        try:
            manifest = await read_remote_json(nc, f"{REMOTE_ROOT}/versions/{product_id}/{product_id}_{version}.json")
        except NextcloudException as e:
            sys.exit(f"Cannot read the manifest of {product_id} {version} ({e}); nothing is judged unused.")
        if manifest.get("package_mode") == PACKAGE_MODE:
            wheels.update(wheel["file"] for wheel in manifest.get("wheels", []))
    archives = {(await read_remote_json(nc, f"{PYTHON_DESCRIPTOR_REMOTE}/{name}"))["file"]
                for name in await list_remote(nc, PYTHON_DESCRIPTOR_REMOTE)
                if name.startswith("python_") and name.endswith(".json")}
    return wheels, archives


def cmd_gc(args: argparse.Namespace) -> None:
    """Lists the unused wheels and Python archives on the server; removes them only with --delete."""

    async def run() -> None:
        nc = await get_nc_client()
        wheels, archives = await referenced_runtime_files(nc)
        if not wheels:
            sys.exit("No app version is listed in suite_manifest.json; refusing to judge any wheel unused.")
        unused = {WHEELS_REMOTE: sorted(set(await list_remote(nc, WHEELS_REMOTE)) - wheels),
                  PYTHON_REMOTE: sorted(set(await list_remote(nc, PYTHON_REMOTE)) - archives)}
        for folder, names in unused.items():
            for name in names:
                logger.info(f"Unused: {folder}/{name}")
                if args.delete:
                    await nc.files.delete(f"{folder}/{name}")
        total = sum(map(len, unused.values()))
        logger.success(f"{total} unused file(s) {'deleted' if args.delete else 'found; add --delete to remove them'}.")

    asyncio.run(run())


def read_exe_version(exe: Path) -> str:
    """Returns the file version of a Windows exe as dotted text; a zero fourth part is dropped (1.1.1.0 -> 1.1.1)."""
    import ctypes
    from ctypes import wintypes

    class FixedFileInfo(ctypes.Structure):
        _fields_ = [(name, wintypes.DWORD) for name in (
            "signature", "struc_version", "file_version_ms", "file_version_ls", "product_version_ms",
            "product_version_ls", "file_flags_mask", "file_flags", "file_os", "file_type", "file_subtype",
            "file_date_ms", "file_date_ls")]

    api = ctypes.WinDLL("version")
    size = api.GetFileVersionInfoSizeW(str(exe), None)
    if not size:
        raise ValueError(f"{exe} has no version information")
    data = ctypes.create_string_buffer(size)
    api.GetFileVersionInfoW(str(exe), 0, size, data)
    block, length = ctypes.c_void_p(), wintypes.UINT()
    api.VerQueryValueW(data, "\\", ctypes.byref(block), ctypes.byref(length))
    info = ctypes.cast(block, ctypes.POINTER(FixedFileInfo)).contents
    parts = [info.file_version_ms >> 16, info.file_version_ms & 0xFFFF,
             info.file_version_ls >> 16, info.file_version_ls & 0xFFFF]
    return ".".join(map(str, parts[:3] if parts[3] == 0 else parts))


def build_zip_package(folder: Path, product_id: str, version: str, out_dir: Path,
                      obsolete: list[str] | None = None) -> tuple[Path, Path]:
    """Zips the content of a folder and writes the zip-mode manifest of the product.

    Returns (manifest path, zip path); the manifest lists the zip with its SHA256 and the obsolete file names.
    """
    zip_name = f"{product_id}_{version}.zip"
    zip_path = out_dir / zip_name
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for item in sorted(folder.rglob("*")):
            if item.is_file():
                archive.write(item, item.relative_to(folder))
                logger.info(f"  + {item.relative_to(folder).as_posix()}")
    manifest = {"product_id": product_id, "version": version, "base_url": f"versions/{product_id}/{version}",
                "package_mode": "zip", "files": [{"path": zip_name, "hash": sha256_of(zip_path)}]}
    if obsolete:
        if clash := [n for n in obsolete if (folder / n).exists()]:
            raise ValueError(f"sct_app.toml: obsolete lists files of the package: {', '.join(clash)}")
        manifest["obsolete"] = obsolete
    manifest_path = out_dir / f"{product_id}_{version}.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest_path, zip_path


def release_folder(folder: Path, product_id: str, version: str, upload: bool, obsolete: list[str] | None = None,
                   promote: str | None = None) -> None:
    """Releases a folder as a zip package; uploads only with upload=True (otherwise a dry run).

    With ``promote`` ("prod" or "dev") the version is then set as the current one of that channel in suite_manifest.json.
    """
    out_dir = REPO_ROOT / "release_artifacts"
    out_dir.mkdir(exist_ok=True)
    manifest_path, zip_path = build_zip_package(folder, product_id, version, out_dir, obsolete)
    logger.info(f"{product_id} {version}: {zip_path.name} {zip_path.stat().st_size / 1048576:.1f} MB")

    if not upload:
        logger.info("Dry run: nothing was uploaded. Add --upload to publish.")
        return

    async def run() -> None:
        nc = await get_nc_client()
        suite = await read_promoted_suite(nc, product_id, version, promote) if promote else None
        base = f"{REMOTE_ROOT}/versions/{product_id}"
        if not await upload_all(nc, [(zip_path, f"{base}/{version}/{zip_path.name}")]):
            sys.exit("Upload failed. Manifest not published.")
        if not await upload_all(nc, [(manifest_path, f"{base}/{manifest_path.name}")]):
            sys.exit("Manifest upload failed")
        if suite:
            await publish_suite(nc, suite, out_dir)
            logger.info(f"suite_manifest.json: {product_id} {version} is now current in {promote}")
        logger.success("Done!")

    asyncio.run(run())


def cmd_zip(args: argparse.Namespace) -> None:
    """Releases any build folder as a zip package of the given product."""
    folder = Path(args.folder)
    if not folder.is_dir():
        sys.exit(f"Folder not found: {folder}")
    release_folder(folder, args.product_id, args.version, args.upload, promote=args.promote)


def cmd_dotnet(args: argparse.Namespace) -> None:
    """Builds a .NET project and releases it as a zip package; the version is read from the built exe."""
    spec = load_dotnet_spec(Path(args.module))
    folder = publish_dotnet(spec)
    version = read_exe_version(folder / spec.exe)
    if args.version and args.version != version:
        sys.exit(f"The built {spec.exe} has version {version}, not {args.version}: change the version in the project.")
    release_folder(folder, spec.id, version, args.upload, spec.obsolete, args.promote)


def main() -> None:
    parser = argparse.ArgumentParser(description="Screen Check Suite release tool")
    sub = parser.add_subparsers(dest="command", required=True)

    zipper = sub.add_parser("zip", help="release any build folder as a zip package (dry run unless --upload)")
    zipper.add_argument("folder", help="folder with the published build")
    zipper.add_argument("product_id")
    zipper.add_argument("version")
    zipper.add_argument("--upload", action="store_true", help="publish to the server")
    zipper.add_argument("--promote", choices=sorted(CHANNEL_KEYS), help=PROMOTE_HELP)
    zipper.set_defaults(func=cmd_zip)

    gc = sub.add_parser("gc", help="list (or delete with --delete) wheels and Python archives no listed version uses")
    gc.add_argument("--delete", action="store_true", help="delete the unused files from the server")
    gc.set_defaults(func=cmd_gc)

    upgrade = sub.add_parser("upgrade", help="move the pinned dependency versions of a module to the newest allowed")
    upgrade.add_argument("module", type=resolve_repo, help="repository alias (updater, nextcloud-cli, report-generator, screen-checker) or path of the module repository")
    upgrade.set_defaults(func=cmd_upgrade)

    app = sub.add_parser("app", help="release a repository: Python module or .NET project (dry run unless --upload)")
    app.add_argument("module", type=resolve_repo, help="repository alias (updater, nextcloud-cli, report-generator, screen-checker) or path of the repository")
    app.add_argument("version", nargs="?", help="release version (optional: a .NET project reads it from the built exe, a Python module from pyproject.toml and APP_VERSION)")
    app.add_argument("--upload", action="store_true", help="publish to the server")
    app.add_argument("--promote", choices=sorted(CHANNEL_KEYS), help=PROMOTE_HELP)
    app.set_defaults(func=cmd_app)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
