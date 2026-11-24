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

A module lives in its own repository and describes itself with ``sct_app.toml``. A release uploads
the module code, the wheels of its lock file (only those the server lacks) and a manifest.
Modules are independent: each has its own dependencies and its own lock file.

Commands:
    lock         Resolve the module's requirements.in into requirements.lock with hashes (uses uv).
    app          Release a module version: code files, missing wheels, manifest (published last).
    python-core  Publish the embeddable Python archive of a minor version (once per Python upgrade).

sct_app.toml (in the module repository):
    id = "report_generator"
    python = "3.13"
    requirements = "requirements.in"   # optional
    lock = "requirements.lock"         # optional
    launcher = "ReportGenerator.exe"   # optional: Rust launcher built with cargo, installed at the Updater root
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
LAUNCHER_DIR = REPO_ROOT / "launcher"
REMOTE_ROOT = "SCT/Updater"
WHEELS_REMOTE = f"{REMOTE_ROOT}/runtime/wheels"
PYTHON_REMOTE = f"{REMOTE_ROOT}/runtime/python"
MAX_WORKERS = 8
UPLOAD_RETRIES = 3

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


def sha256_of(path: Path) -> str:
    """Returns the SHA256 of a file as lowercase hex."""
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_spec(module_dir: Path) -> AppSpec:
    """Reads and validates the sct_app.toml of a module repository."""
    path = module_dir / "sct_app.toml"
    if not path.exists():
        raise FileNotFoundError(f"{path} not found; add the descriptor to the module repository.")
    data = tomllib.loads(path.read_text(encoding="utf-8"))
    for key in ("id", "python", "files"):
        if key not in data:
            raise ValueError(f"{path.name}: '{key}' is required")
    if not re.fullmatch(r"\d+\.\d+", data["python"]):
        raise ValueError(f"{path.name}: python must be a minor version like 3.13")
    launcher = data.get("launcher")
    if launcher is not None and not re.fullmatch(r"[^\\/:*?\"<>|]+\.exe", launcher):
        raise ValueError(f"{path.name}: launcher must be a file name ending with .exe")
    return AppSpec(module_dir, data["id"], data["python"],
                   module_dir / data.get("requirements", "requirements.in"),
                   module_dir / data.get("lock", "requirements.lock"), data["files"], launcher)


def build_launcher(app_id: str) -> Path:
    """Builds the Rust launcher for a module with cargo and returns the path of the produced exe.

    The module id is baked into the exe. The build runs inside the launcher folder so that its
    .cargo/config.toml (reproducible linking) applies.
    """
    cargo = shutil.which("cargo")
    if not cargo:
        sys.exit("cargo is required to build the launcher (install Rust from https://rustup.rs).")
    target = REPO_ROOT / "release_artifacts" / "launcher" / app_id
    subprocess.run([cargo, "build", "--release", "--target-dir", str(target)],
                   cwd=LAUNCHER_DIR, env={**os.environ, "SCT_APP": app_id}, check=True)
    return target / "release" / "sct_launcher.exe"


def with_launcher(spec: AppSpec, files: list[tuple[Path, str]], build=build_launcher) -> list[tuple[Path, str]]:
    """Adds the module launcher, installed at the Updater root under its exe name, to the file list."""
    if not spec.launcher:
        return files
    return sorted([*files, (build(spec.id), spec.launcher)], key=lambda item: item[1])


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
    return {
        "product_id": spec.id,
        "version": version,
        "base_url": f"versions/{spec.id}/{version}",
        "package_mode": PACKAGE_MODE,
        "python": spec.python,
        "wheels_url": "runtime/wheels",
        "files": [{"path": rel, "hash": sha256_of(path)} for path, rel in files],
        "wheels": [{"file": w.name, "sha256": sha256_of(w), "size": w.stat().st_size} for w in wheels],
    }


def python_embed(wheelhouse: Path) -> tuple[dict, Path]:
    """Returns the pinned embeddable Python descriptor and its cached zip, downloading it when needed."""
    info = json.loads(PYTHON_EMBED_FILE.read_text(encoding="utf-8"))
    version = info["version"]
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


async def upload_app(nc, manifest: dict, files: list[tuple[Path, str]], wheels: list[Path], manifest_path: Path) -> None:
    """Uploads the code files and the missing wheels, then publishes the manifest last."""
    remote_wheels = await list_remote(nc, WHEELS_REMOTE)
    missing = [w for w in wheels if remote_wheels.get(w.name) != w.stat().st_size]
    base = f"{REMOTE_ROOT}/{manifest['base_url']}"

    jobs = [(path, f"{base}/{rel}") for path, rel in files] + [(w, f"{WHEELS_REMOTE}/{w.name}") for w in missing]
    size_mb = sum(local.stat().st_size for local, _ in jobs) / 1048576
    logger.info(f"{manifest['product_id']} {manifest['version']}: {len(files)} code files, {len(wheels)} wheels "
                f"({len(missing)} to upload) | total upload {size_mb:.1f} MB")

    if not await upload_all(nc, jobs):
        logger.error("Some uploads failed. Manifest not published.")
        sys.exit(1)
    target = f"{REMOTE_ROOT}/versions/{manifest['product_id']}/{manifest_path.name}"
    if not await upload_all(nc, [(manifest_path, target)]):
        sys.exit("Manifest upload failed")
    logger.success("Done!")


async def upload_python_core(nc, info: dict, archive: Path, descriptor: Path) -> None:
    """Uploads the embeddable Python archive and its descriptor (descriptor last)."""
    remote_python = await list_remote(nc, PYTHON_REMOTE)
    jobs = [] if remote_python.get(archive.name) == archive.stat().st_size else [(archive, f"{PYTHON_REMOTE}/{archive.name}")]
    logger.info(f"Python {info['version']}: {'already on the server' if not jobs else 'uploading the archive'}")
    if not await upload_all(nc, jobs):
        sys.exit("Archive upload failed")
    if not await upload_all(nc, [(descriptor, f"{PYTHON_REMOTE}/{descriptor.name}")]):
        sys.exit("Descriptor upload failed")
    logger.success("Done!")


# --- Commands ---

def cmd_lock(args: argparse.Namespace) -> None:
    """Resolves the module's requirements.in into requirements.lock with hashes."""
    spec = load_spec(Path(args.module))
    uv = shutil.which("uv")
    if not uv:
        sys.exit("uv is required to compile the lock file.")
    command = [uv, "pip", "compile", str(spec.requirements), "--python-version", spec.python,
               "--python-platform", "windows", "--only-binary", ":all:", "--generate-hashes", "-o", str(spec.lock)]
    if args.upgrade:
        command.append("--upgrade")
    subprocess.run(command, check=True)
    logger.success(f"Lock written: {spec.lock}")


def cmd_app(args: argparse.Namespace) -> None:
    """Prepares a module release and uploads it."""
    spec = load_spec(Path(args.module))
    if not spec.lock.exists():
        sys.exit(f"{spec.lock.name} not found; run the 'lock' command first.")

    files = with_launcher(spec, collect_files(spec))
    wheels = fill_wheelhouse(parse_lock(spec.lock.read_text(encoding="utf-8")), Path(args.wheelhouse), spec.python)
    manifest = build_manifest(spec, args.version, files, wheels)

    out_dir = REPO_ROOT / "release_artifacts"
    out_dir.mkdir(exist_ok=True)
    manifest_path = out_dir / f"{spec.id}_{args.version}.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    logger.info(f"Manifest: {manifest_path.name} | {len(files)} code files | {len(wheels)} wheels | Python {spec.python}")

    if not args.upload and input("Upload? (y/n): ").lower() != "y":
        return

    async def run() -> None:
        await upload_app(await get_nc_client(), manifest, files, wheels, manifest_path)

    asyncio.run(run())


def cmd_python_core(args: argparse.Namespace) -> None:
    """Publishes the pinned embeddable Python archive and a descriptor named after its minor version."""
    info, archive = python_embed(Path(args.wheelhouse))
    minor = ".".join(info["version"].split(".")[:2])
    out_dir = REPO_ROOT / "release_artifacts"
    out_dir.mkdir(exist_ok=True)
    descriptor = out_dir / f"python_{minor}.json"
    descriptor.write_text(json.dumps({"version": info["version"], "file": archive.name, "sha256": info["sha256"],
                                      "size": archive.stat().st_size}, indent=2) + "\n", encoding="utf-8")

    if not args.upload and input("Upload? (y/n): ").lower() != "y":
        return

    async def run() -> None:
        await upload_python_core(await get_nc_client(), info, archive, descriptor)

    asyncio.run(run())


def main() -> None:
    default_wheelhouse = os.environ.get("SCT_WHEELHOUSE", str(Path.home() / ".sct_wheelhouse"))
    parser = argparse.ArgumentParser(description="Screen Check Suite module release tool")
    sub = parser.add_subparsers(dest="command", required=True)

    lock = sub.add_parser("lock", help="resolve requirements.in of a module into requirements.lock")
    lock.add_argument("module", help="path of the module repository")
    lock.add_argument("--upgrade", action="store_true", help="allow package versions to change")
    lock.set_defaults(func=cmd_lock)

    app = sub.add_parser("app", help="release a module version")
    app.add_argument("module", help="path of the module repository")
    app.add_argument("version")
    app.add_argument("--wheelhouse", default=default_wheelhouse, help="local wheel cache (env SCT_WHEELHOUSE)")
    app.add_argument("--upload", action="store_true", help="upload without the confirmation prompt")
    app.set_defaults(func=cmd_app)

    core = sub.add_parser("python-core", help="publish the embeddable Python archive")
    core.add_argument("--wheelhouse", default=default_wheelhouse, help="local cache (env SCT_WHEELHOUSE)")
    core.add_argument("--upload", action="store_true", help="upload without the confirmation prompt")
    core.set_defaults(func=cmd_python_core)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
