import asyncio
import hashlib
import json
import sys
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest
from nc_py_api import NextcloudException

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import app_release as ar  # noqa: E402


def make_wheel(folder: Path, name: str, content: bytes = b"x") -> Path:
    """Creates a minimal wheel file and returns its path."""
    path = folder / name
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("pkg/__init__.py", content)
    return path


def pin_for(wheel: Path, name: str, version: str) -> ar.Pin:
    return ar.Pin(name, version, {ar.sha256_of(wheel)})


def write_module(root: Path, descriptor: str) -> Path:
    """Creates a module repository with a few source files and the given sct_app.toml."""
    (root / "src" / "tests").mkdir(parents=True)
    (root / "src" / "__pycache__").mkdir()
    (root / "main.py").write_text("print('main')", encoding="utf-8")
    (root / "src" / "calc.py").write_text("x = 1", encoding="utf-8")
    (root / "src" / "tests" / "test_calc.py").write_text("t", encoding="utf-8")
    (root / "src" / "__pycache__" / "calc.cpython-313.pyc").write_bytes(b"pyc")
    (root / "config").mkdir()
    (root / "config" / "template.html").write_text("<html/>", encoding="utf-8")
    (root / "sct_app.toml").write_text(descriptor, encoding="utf-8")
    return root


DESCRIPTOR = '''
id = "demo"
python = "3.13"

[[files]]
src = "main.py"
dst = "__main__.py"

[[files]]
src = "src"
dst = "src"
exclude = ["tests"]

[[files]]
src = "config/template.html"
dst = "config/template.html"
target = "root"
'''


# --- descriptor ---

def test_load_spec_reads_the_descriptor(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    assert spec.id == "demo" and spec.python == "3.13"
    assert spec.requirements.name == "requirements.in" and spec.lock.name == "requirements.lock"


def test_load_spec_requires_the_descriptor(tmp_path):
    with pytest.raises(FileNotFoundError):
        ar.load_spec(tmp_path)


@pytest.mark.parametrize("text, message", [
    ('python = "3.13"\n[[files]]\nsrc = "a"\n', "'id' is required"),
    ('id = "x"\n[[files]]\nsrc = "a"\n', "'python' is required"),
    ('id = "x"\npython = "3.13.5"\n[[files]]\nsrc = "a"\n', "minor version"),
])
def test_load_spec_rejects_invalid_descriptors(tmp_path, text, message):
    (tmp_path / "sct_app.toml").write_text(text, encoding="utf-8")

    with pytest.raises(ValueError, match=message):
        ar.load_spec(tmp_path)


# --- code files ---

def test_collect_files_maps_sources_to_install_paths(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    installed = [rel for _, rel in ar.collect_files(spec)]

    assert installed == ["apps/demo/__main__.py", "apps/demo/src/calc.py", "config/template.html"]


def test_collect_files_skips_caches_and_excluded_folders(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    installed = " ".join(rel for _, rel in ar.collect_files(spec))

    assert "__pycache__" not in installed and ".pyc" not in installed and "tests" not in installed


def test_collect_files_fails_on_a_missing_source(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, 'id = "d"\npython = "3.13"\n[[files]]\nsrc = "nope.py"\n'))

    with pytest.raises(FileNotFoundError):
        ar.collect_files(spec)


# --- lock and wheels ---

def test_parse_lock_requires_hashes():
    with pytest.raises(ValueError, match="hashes"):
        ar.parse_lock("numpy==2.3.3\n")


def test_parse_lock_reads_pins_with_hashes():
    text = "Some_Package==1.0 \\\n    --hash=sha256:" + "a" * 64 + " \\\n    --hash=sha256:" + "b" * 64 + "\n    # via x\n"

    pin = ar.parse_lock(text)[0]

    assert pin.name == "some-package" and pin.version == "1.0" and len(pin.hashes) == 2


def test_find_wheel_accepts_only_a_locked_hash(tmp_path):
    wheel = make_wheel(tmp_path, "pkg-1.0-py3-none-any.whl")

    assert ar.find_wheel(tmp_path, pin_for(wheel, "pkg", "1.0")) == wheel
    assert ar.find_wheel(tmp_path, ar.Pin("pkg", "1.0", {"b" * 64})) is None
    assert ar.find_wheel(tmp_path, pin_for(wheel, "pkg", "2.0")) is None


def test_fill_wheelhouse_uses_the_cache_without_downloading(tmp_path, monkeypatch):
    wheel = make_wheel(tmp_path, "pkg-1.0-py3-none-any.whl")
    monkeypatch.setattr(ar.subprocess, "run", lambda *a, **k: pytest.fail("must not download"))

    assert ar.fill_wheelhouse([pin_for(wheel, "pkg", "1.0")], tmp_path, "3.13") == [wheel]


def test_fill_wheelhouse_fails_when_the_download_does_not_match_the_lock(tmp_path, monkeypatch):
    monkeypatch.setattr(ar.subprocess, "run", lambda *a, **k: None)

    with pytest.raises(RuntimeError, match="accepted hash"):
        ar.fill_wheelhouse([ar.Pin("pkg", "1.0", {"c" * 64})], tmp_path, "3.13")


# --- manifest ---

def test_manifest_describes_code_and_wheels(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))
    files = ar.collect_files(spec)
    wheel = make_wheel(tmp_path, "a-1.0-py3-none-any.whl", b"1")

    manifest = ar.build_manifest(spec, "2.0.0", files, [wheel])

    assert manifest["package_mode"] == "app" and manifest["python"] == "3.13"
    assert manifest["base_url"] == "versions/demo/2.0.0" and manifest["wheels_url"] == "runtime/wheels"
    assert manifest["files"][0] == {"path": "apps/demo/__main__.py",
                                    "hash": hashlib.sha256(b"print('main')").hexdigest()}
    assert manifest["wheels"] == [{"file": wheel.name, "sha256": ar.sha256_of(wheel), "size": wheel.stat().st_size}]


# --- upload ---

class FakeFiles:
    def __init__(self, existing=None, fail_wheels=False):
        self.tree = dict(existing or {})
        self.log: list[str] = []
        self.fail_wheels = fail_wheels

    async def listdir(self, path):
        prefix = path.rstrip("/") + "/"
        kids = {p[len(prefix):]: s for p, s in self.tree.items() if p.startswith(prefix) and "/" not in p[len(prefix):]}
        if not kids:
            raise NextcloudException(404, "Not Found")
        return [SimpleNamespace(name=n, is_dir=False, info=SimpleNamespace(content_length=s)) for n, s in kids.items()]

    async def mkdir(self, path):
        return None

    async def upload_stream(self, remote, local):
        if self.fail_wheels and remote.endswith(".whl"):
            raise RuntimeError("boom")
        self.tree[remote] = Path(local).stat().st_size
        self.log.append(remote)


def release(tmp_path, files_api: FakeFiles):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))
    code = ar.collect_files(spec)
    wheels = [make_wheel(tmp_path, f"w{i}-1.0-py3-none-any.whl", c) for i, c in enumerate((b"1", b"22"))]
    manifest = ar.build_manifest(spec, "1.0.0", code, wheels)
    manifest_path = tmp_path / "demo_1.0.0.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    asyncio.run(ar.upload_app(SimpleNamespace(files=files_api), manifest, code, wheels, manifest_path))
    return wheels


def test_release_uploads_code_wheels_and_the_manifest_last(tmp_path):
    api = FakeFiles()

    release(tmp_path, api)

    assert api.log[-1] == "SCT/Updater/versions/demo/demo_1.0.0.json"
    assert "SCT/Updater/versions/demo/1.0.0/apps/demo/__main__.py" in api.log
    assert "SCT/Updater/versions/demo/1.0.0/config/template.html" in api.log
    assert sum(1 for r in api.log if r.startswith(ar.WHEELS_REMOTE)) == 2


def test_release_skips_wheels_the_server_has(tmp_path):
    first = make_wheel(tmp_path, "w0-1.0-py3-none-any.whl", b"1")
    api = FakeFiles({f"{ar.WHEELS_REMOTE}/{first.name}": first.stat().st_size})

    release(tmp_path, api)

    assert not any(r.endswith(first.name) for r in api.log)
    assert any(r.endswith("w1-1.0-py3-none-any.whl") for r in api.log)


def test_release_replaces_a_truncated_wheel(tmp_path):
    first = make_wheel(tmp_path, "w0-1.0-py3-none-any.whl", b"1")
    api = FakeFiles({f"{ar.WHEELS_REMOTE}/{first.name}": 1})

    release(tmp_path, api)

    assert any(r.endswith(first.name) for r in api.log)


def test_failed_wheel_upload_blocks_the_manifest(tmp_path, monkeypatch):
    monkeypatch.setattr(ar, "UPLOAD_RETRIES", 1)
    api = FakeFiles(fail_wheels=True)

    with pytest.raises(SystemExit):
        release(tmp_path, api)

    assert not any(r.endswith("demo_1.0.0.json") for r in api.log)


def test_python_core_uploads_the_archive_then_the_descriptor(tmp_path):
    archive = tmp_path / "python-3.13.5-embed-amd64.zip"
    archive.write_bytes(b"zip")
    descriptor = tmp_path / "python_3.13.json"
    descriptor.write_text("{}", encoding="utf-8")
    api = FakeFiles()

    asyncio.run(ar.upload_python_core(SimpleNamespace(files=api), {"version": "3.13.5"}, archive, descriptor))

    assert api.log == [f"{ar.PYTHON_REMOTE}/{archive.name}", f"{ar.PYTHON_REMOTE}/{descriptor.name}"]


def test_python_core_is_not_uploaded_twice(tmp_path):
    archive = tmp_path / "python-3.13.5-embed-amd64.zip"
    archive.write_bytes(b"zip")
    descriptor = tmp_path / "python_3.13.json"
    descriptor.write_text("{}", encoding="utf-8")
    api = FakeFiles({f"{ar.PYTHON_REMOTE}/{archive.name}": 3})

    asyncio.run(ar.upload_python_core(SimpleNamespace(files=api), {"version": "3.13.5"}, archive, descriptor))

    assert api.log == [f"{ar.PYTHON_REMOTE}/{descriptor.name}"]


# --- launcher ---

def test_load_spec_reads_the_launcher_name(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, 'launcher = "Demo.exe"\n' + DESCRIPTOR))

    assert spec.launcher == "Demo.exe"


@pytest.mark.parametrize("name", ["Demo", "dir/Demo.exe", "Demo.bat"])
def test_load_spec_rejects_an_invalid_launcher_name(tmp_path, name):
    descriptor = f'launcher = "{name}"\n' + DESCRIPTOR

    with pytest.raises(ValueError, match="launcher"):
        ar.load_spec(write_module(tmp_path, descriptor))


def test_with_launcher_adds_the_exe_at_the_root(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, 'launcher = "Demo.exe"\n' + DESCRIPTOR))
    built = tmp_path / "built.exe"
    built.write_bytes(b"MZ")

    files = ar.with_launcher(spec, ar.collect_files(spec), build=lambda app_id: built if app_id == "demo" else None)

    assert (built, "Demo.exe") in files
    assert [rel for _, rel in files] == sorted(rel for _, rel in files)


def test_with_launcher_does_nothing_without_a_launcher(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))
    files = ar.collect_files(spec)

    assert ar.with_launcher(spec, files, build=lambda app_id: pytest.fail("must not build")) == files
