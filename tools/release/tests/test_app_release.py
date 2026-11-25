import argparse
import asyncio
import hashlib
import json
import re
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
    (root / ".python-version").write_text("3.13\n", encoding="utf-8")
    return root


DESCRIPTOR = '''
id = "demo"

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


# --- version ---

def versioned_module(root: Path, pyproject: str | None, app_version: str | None) -> ar.AppSpec:
    """Creates a demo module whose version lives in pyproject.toml and/or APP_VERSION of main.py."""
    write_module(root, DESCRIPTOR)
    if pyproject:
        (root / "pyproject.toml").write_text(f'[project]\nname = "demo"\nversion = "{pyproject}"\n', encoding="utf-8")
    if app_version:
        (root / "main.py").write_text(f'APP_VERSION = "{app_version}"\n', encoding="utf-8")
    return ar.load_spec(root)


def test_version_defaults_to_the_one_the_module_keeps(tmp_path):
    assert ar.resolve_module_version(versioned_module(tmp_path, "1.5.3", "1.5.3"), None) == "1.5.3"


def test_version_can_come_from_a_single_place(tmp_path):
    assert ar.resolve_module_version(versioned_module(tmp_path, None, "2.0.1"), None) == "2.0.1"


def test_disagreeing_version_places_stop_the_release(tmp_path):
    with pytest.raises(SystemExit, match="disagree"):
        ar.resolve_module_version(versioned_module(tmp_path, "1.5.3", "1.5.2"), None)


def test_no_version_anywhere_requires_it_on_the_command_line(tmp_path):
    with pytest.raises(SystemExit, match="version is required"):
        ar.resolve_module_version(versioned_module(tmp_path, None, None), None)


def test_a_given_version_wins_over_the_files(tmp_path):
    assert ar.resolve_module_version(versioned_module(tmp_path, "1.5.3", "1.5.3"), "1.6.0") == "1.6.0"


# --- descriptor ---

def test_load_spec_reads_the_descriptor(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    assert spec.id == "demo" and spec.python == "3.13"
    assert spec.requirements.name == "requirements.in" and spec.lock.name == "requirements.lock"


def test_load_spec_requires_the_descriptor(tmp_path):
    with pytest.raises(FileNotFoundError):
        ar.load_spec(tmp_path)


@pytest.mark.parametrize("text, message", [
    ('[[files]]\nsrc = "a"\n', "'id' is required"),
    ('id = "x"\n', "'files' is required"),
])
def test_load_spec_rejects_invalid_descriptors(tmp_path, text, message):
    (tmp_path / "sct_app.toml").write_text(text, encoding="utf-8")
    (tmp_path / ".python-version").write_text("3.13\n", encoding="utf-8")

    with pytest.raises(ValueError, match=message):
        ar.load_spec(tmp_path)


@pytest.mark.parametrize("content, minor", [("3.13\n", "3.13"), ("3.13.5\n", "3.13"), (" 3.12", "3.12")])
def test_python_minor_comes_from_python_version(tmp_path, content, minor):
    (tmp_path / ".python-version").write_text(content, encoding="utf-8")

    assert ar.read_python_minor(tmp_path) == minor


def test_python_version_file_is_required(tmp_path):
    with pytest.raises(FileNotFoundError, match="python-version"):
        ar.read_python_minor(tmp_path)


def test_python_version_must_look_like_a_version(tmp_path):
    (tmp_path / ".python-version").write_text("system\n", encoding="utf-8")

    with pytest.raises(ValueError, match="Python version"):
        ar.read_python_minor(tmp_path)


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


def with_obsolete(value: str) -> str:
    return f"""obsolete = {value}
""" + DESCRIPTOR


def test_obsolete_files_are_written_to_the_manifest(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, with_obsolete('["Demo_old.exe"]')))

    manifest = ar.build_manifest(spec, "2.0.0", ar.collect_files(spec), [])

    assert manifest["obsolete"] == ["Demo_old.exe"]
    plain = ar.load_spec(write_module(tmp_path / "b", DESCRIPTOR))
    assert "obsolete" not in ar.build_manifest(plain, "2.0.0", ar.collect_files(plain), [])


@pytest.mark.parametrize("value", ['"a.exe"', '["sub/a.exe"]', '[".."]', '[1]'])
def test_obsolete_must_be_a_list_of_plain_file_names(tmp_path, value):
    with pytest.raises(ValueError, match="obsolete"):
        ar.load_spec(write_module(tmp_path, with_obsolete(value)))


def test_obsolete_cannot_name_a_file_of_the_package(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, with_obsolete('["__main__.py"]')))

    with pytest.raises(ValueError, match="obsolete"):
        ar.build_manifest(spec, "1.0.0", [(tmp_path / "main.py", "__main__.py")], [])


def test_zip_package_carries_the_obsolete_files(tmp_path):
    folder = tmp_path / "build"
    folder.mkdir()
    (folder / "New.exe").write_bytes(b"x")

    manifest_path, _ = ar.build_zip_package(folder, "demo", "1.0.0", tmp_path, ["Old.exe"])

    assert json.loads(manifest_path.read_text(encoding="utf-8"))["obsolete"] == ["Old.exe"]
    with pytest.raises(ValueError, match="obsolete"):
        ar.build_zip_package(folder, "demo", "1.0.1", tmp_path, ["New.exe"])


def test_resolve_repo_accepts_aliases_in_any_case_and_plain_paths(tmp_path, monkeypatch):
    monkeypatch.setitem(ar.REPO_ALIASES, "demo", tmp_path)

    assert ar.resolve_repo("demo") == tmp_path
    assert ar.resolve_repo("DEMO") == tmp_path
    assert ar.resolve_repo(str(tmp_path)) == tmp_path


def test_resolve_repo_rejects_an_unknown_name_and_lists_the_aliases():
    with pytest.raises(argparse.ArgumentTypeError, match="aliases: updater"):
        ar.resolve_repo("no-such-repository")


def test_alias_key_ignores_case_underscores_and_the_sct_and_suite_prefixes():
    for spelling in ("updater", "Updater", "sct-updater", "SCT_Updater", "suite_updater"):
        assert ar.alias_key(spelling) == "updater"
    assert ar.alias_key("Report_Generator") == ar.alias_key("sct-report-generator") == "report-generator"


def test_every_alias_resolves_to_a_product_repository_by_name_and_product_id():
    assert ar.REPO_ALIASES["updater"] == ar.REPO_ROOT
    for spelling, alias in (("nextcloud_cli", "nextcloud-cli"), ("report_generator", "report-generator"),
                            ("screen_checker", "screen-checker"), ("suite_updater", "updater")):
        assert ar.REPO_ALIASES[ar.alias_key(spelling)] == ar.REPO_ALIASES[alias]


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
    python_zip = tmp_path / "python-3.13.5-embed-amd64.zip"
    python_zip.write_bytes(b"zip")
    python_json = ar.python_descriptor({"version": "3.13.5", "sha256": "ab"}, python_zip, tmp_path)
    asyncio.run(ar.upload_app(SimpleNamespace(files=files_api), manifest, code, wheels, manifest_path, python_zip, python_json))
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


def test_release_uploads_python_when_the_server_lacks_it(tmp_path):
    api = FakeFiles()

    release(tmp_path, api)

    assert f"{ar.PYTHON_REMOTE}/python-3.13.5-embed-amd64.zip" in api.log
    assert f"{ar.REMOTE_ROOT}/runtime/python_3.13.json" in api.log
    assert api.log[-1].endswith("demo_1.0.0.json")


def test_release_does_not_upload_python_twice_but_refreshes_the_descriptor(tmp_path):
    api = FakeFiles({f"{ar.PYTHON_REMOTE}/python-3.13.5-embed-amd64.zip": 3})

    release(tmp_path, api)

    assert f"{ar.PYTHON_REMOTE}/python-3.13.5-embed-amd64.zip" not in api.log
    assert f"{ar.REMOTE_ROOT}/runtime/python_3.13.json" in api.log


def test_python_descriptor_describes_the_archive(tmp_path):
    archive = tmp_path / "python-3.13.5-embed-amd64.zip"
    archive.write_bytes(b"zip")

    path = ar.python_descriptor({"version": "3.13.5", "sha256": "ab"}, archive, tmp_path)

    assert path.name == "python_3.13.json"
    assert json.loads(path.read_text(encoding="utf-8")) == {
        "version": "3.13.5", "file": archive.name, "sha256": "ab", "size": 3}


def test_python_embed_rejects_a_version_of_another_minor(tmp_path, monkeypatch):
    pinned = tmp_path / "python_embed.json"
    pinned.write_text('{"version": "3.14.0", "sha256": "ab"}', encoding="utf-8")
    monkeypatch.setattr(ar, "PYTHON_EMBED_FILE", pinned)

    with pytest.raises(ValueError, match="uses Python 3.13"):
        ar.python_embed(tmp_path, "3.13")


def test_wheelhouse_comes_from_the_environment(monkeypatch, tmp_path):
    monkeypatch.setenv("SCT_WHEELHOUSE", str(tmp_path))

    assert ar.wheelhouse_dir() == tmp_path


def test_wheelhouse_defaults_to_the_home_folder(monkeypatch):
    monkeypatch.delenv("SCT_WHEELHOUSE", raising=False)

    assert ar.wheelhouse_dir() == Path.home() / ".sct_wheelhouse"


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

    files = ar.with_launcher(spec, ar.collect_files(spec), "1.6.0",
                             build=lambda s, v: built if (s.id, v) == ("demo", "1.6.0") else None)

    assert (built, "Demo.exe") in files
    assert [rel for _, rel in files] == sorted(rel for _, rel in files)


def test_with_launcher_does_nothing_without_a_launcher(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))
    files = ar.collect_files(spec)

    assert ar.with_launcher(spec, files, "1.6.0", build=lambda s, v: pytest.fail("must not build")) == files


# --- automatic lock maintenance ---

def spec_with_requirements(tmp_path, requirements="loguru\n"):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))
    spec.requirements.write_text(requirements, encoding="utf-8")
    return spec


def write_lock(spec, source_hash):
    mark = f"{ar.LOCK_SOURCE_MARK}{source_hash}\n" if source_hash else ""
    spec.lock.write_text(f"{mark}loguru==0.7.3 \\n    --hash=sha256:{'a' * 64}\n", encoding="utf-8")


def test_lock_is_compiled_when_missing(tmp_path):
    spec = spec_with_requirements(tmp_path)
    calls = []

    changed = ar.ensure_lock(spec, compile_fn=lambda s: calls.append(s))

    assert changed and len(calls) == 1


def test_lock_is_kept_when_it_matches_requirements(tmp_path):
    spec = spec_with_requirements(tmp_path)
    write_lock(spec, ar.sha256_of(spec.requirements))

    changed = ar.ensure_lock(spec, compile_fn=lambda s: pytest.fail("must not compile"))

    assert not changed


def test_lock_is_recompiled_when_requirements_changed(tmp_path):
    spec = spec_with_requirements(tmp_path)
    write_lock(spec, ar.sha256_of(spec.requirements))
    spec.requirements.write_text("loguru\nnumpy\n", encoding="utf-8")
    calls = []

    changed = ar.ensure_lock(spec, compile_fn=lambda s: calls.append(s))

    assert changed and len(calls) == 1


def test_lock_without_a_record_is_recompiled(tmp_path):
    spec = spec_with_requirements(tmp_path)
    write_lock(spec, None)
    calls = []

    ar.ensure_lock(spec, compile_fn=lambda s: calls.append(s))

    assert len(calls) == 1


def test_missing_requirements_file_is_reported(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    with pytest.raises(FileNotFoundError, match="requirements.in"):
        ar.ensure_lock(spec, compile_fn=lambda s: pytest.fail("must not compile"))


def test_compile_lock_records_the_source_and_keeps_pins_by_default(tmp_path, monkeypatch):
    spec = spec_with_requirements(tmp_path)
    monkeypatch.setattr(ar.shutil, "which", lambda name: "uv")
    commands = []

    def fake_run(command, check):
        commands.append(command)
        spec.lock.write_text("loguru==0.7.3 \\n    --hash=sha256:" + "a" * 64 + "\n", encoding="utf-8")

    ar.compile_lock(spec, run=fake_run)
    text = spec.lock.read_text(encoding="utf-8")

    assert "--upgrade" not in commands[0] and "--no-header" in commands[0] and "--generate-hashes" in commands[0]
    assert ar.lock_source_hash(text) == ar.sha256_of(spec.requirements)
    assert ar.parse_lock(text)[0].name == "loguru"


def test_compile_lock_upgrade_flag_is_passed(tmp_path, monkeypatch):
    spec = spec_with_requirements(tmp_path)
    monkeypatch.setattr(ar.shutil, "which", lambda name: "uv")
    commands = []

    def fake_run(command, check):
        commands.append(command)
        spec.lock.write_text("loguru==0.7.3 \\n    --hash=sha256:" + "a" * 64 + "\n", encoding="utf-8")

    ar.compile_lock(spec, upgrade=True, run=fake_run)

    assert "--upgrade" in commands[0]


# --- launcher icon and properties ---

def test_title_and_icon_are_read_from_the_descriptor(tmp_path):
    module = write_module(tmp_path, 'title = "Demo App"\nicon = "assets/app.ico"\n' + DESCRIPTOR)
    (module / "assets").mkdir()
    (module / "assets" / "app.ico").write_bytes(b"ico")

    spec = ar.load_spec(module)

    assert spec.title == "Demo App" and spec.icon == module / "assets" / "app.ico"


def test_title_and_icon_are_optional(tmp_path):
    spec = ar.load_spec(write_module(tmp_path, DESCRIPTOR))

    assert spec.title is None and spec.icon is None


@pytest.mark.parametrize("icon", ["assets/missing.ico", "assets/app.png"])
def test_icon_must_be_an_existing_ico_file(tmp_path, icon):
    module = write_module(tmp_path, f'icon = "{icon}"\n' + DESCRIPTOR)
    (module / "assets").mkdir()
    (module / "assets" / "app.png").write_bytes(b"png")

    with pytest.raises(FileNotFoundError, match="icon"):
        ar.load_spec(module)


def test_build_launcher_passes_the_properties_and_names_the_exe(tmp_path, monkeypatch):
    module = write_module(tmp_path / "module", 'launcher = "Demo.exe"\ntitle = "Demo App"\nicon = "assets/app.ico"\n' + DESCRIPTOR)
    (module / "assets").mkdir()
    (module / "assets" / "app.ico").write_bytes(b"ico")
    spec = ar.load_spec(module)
    monkeypatch.setattr(ar, "REPO_ROOT", tmp_path / "repo")
    monkeypatch.setattr(ar.shutil, "which", lambda name: "cargo")
    seen = {}

    def fake_run(command, cwd, env, check):
        seen.update(env=env, cwd=cwd)
        release = tmp_path / "repo" / "release_artifacts" / "launcher" / "demo" / "release"
        release.mkdir(parents=True)
        (release / "sct_launcher.exe").write_bytes(b"MZ")

    monkeypatch.setattr(ar.subprocess, "run", fake_run)

    built = ar.build_launcher(spec, "1.6.0")

    assert built.name == "Demo.exe" and built.read_bytes() == b"MZ"
    assert not (built.parent / "sct_launcher.exe").exists()
    assert seen["env"]["SCT_APP"] == "demo" and seen["env"]["SCT_EXE"] == "Demo.exe"
    assert seen["env"]["SCT_VERSION"] == "1.6.0"
    assert seen["env"]["SCT_TITLE"] == "Demo App" and seen["env"]["SCT_ICON"].endswith("app.ico")


def test_build_launcher_title_defaults_to_the_id_and_icon_is_omitted(tmp_path, monkeypatch):
    spec = ar.load_spec(write_module(tmp_path / "module", 'launcher = "Demo.exe"\n' + DESCRIPTOR))
    monkeypatch.setattr(ar, "REPO_ROOT", tmp_path / "repo")
    monkeypatch.setattr(ar.shutil, "which", lambda name: "cargo")
    seen = {}

    def fake_run(command, cwd, env, check):
        seen.update(env=env)
        release = tmp_path / "repo" / "release_artifacts" / "launcher" / "demo" / "release"
        release.mkdir(parents=True)
        (release / "sct_launcher.exe").write_bytes(b"MZ")

    monkeypatch.setattr(ar.subprocess, "run", fake_run)

    ar.build_launcher(spec, "1.6.0")

    assert seen["env"]["SCT_TITLE"] == "demo" and "SCT_ICON" not in seen["env"]


# --- Updater release ---

def test_read_exe_version_reads_the_file_version_of_an_exe():
    version = ar.read_exe_version(Path(sys.executable))

    assert re.fullmatch(r"\d+\.\d+\.\d+(\.\d+)?", version)


def test_read_exe_version_rejects_a_file_without_version_information(tmp_path):
    plain = tmp_path / "plain.exe"
    plain.write_bytes(b"MZ")

    with pytest.raises(ValueError, match="version"):
        ar.read_exe_version(plain)


def test_zip_package_holds_the_whole_folder(tmp_path):
    build = tmp_path / "build"
    (build / "config").mkdir(parents=True)
    for name in ("Screen Checker.exe", "appsettings.json", "config/a.json"):
        (build / name).write_bytes(b"data")
    out = tmp_path / "out"
    out.mkdir()

    manifest_path, zip_path = ar.build_zip_package(build, "screen_checker", "1.4.2", out)

    with zipfile.ZipFile(zip_path) as archive:
        assert sorted(archive.namelist()) == ["Screen Checker.exe", "appsettings.json", "config/a.json"]
    assert json.loads(manifest_path.read_text(encoding="utf-8")) == {
        "product_id": "screen_checker", "version": "1.4.2", "base_url": "versions/screen_checker/1.4.2",
        "package_mode": "zip", "files": [{"path": "screen_checker_1.4.2.zip", "hash": ar.sha256_of(zip_path)}]}


def write_dotnet_repo(tmp_path, extra=""):
    repo = tmp_path / "repo"
    (repo / "src").mkdir(parents=True)
    (repo / "src" / "App.csproj").write_text("<Project/>", encoding="utf-8")
    (repo / "sct_app.toml").write_text(
        'kind = "dotnet"\nid = "demo_app"\nproject = "src/App.csproj"\nexe = "Demo App.exe"\n' + extra, encoding="utf-8")
    return repo


def fake_publish(tmp_path, monkeypatch, names, which="C:/vs/MSBuild.exe"):
    monkeypatch.setattr(ar, "BUILD_DIR", tmp_path / "build")
    monkeypatch.setattr(ar.shutil, "which", lambda name: which)
    calls = []

    def run(command, check):
        calls.append(command)
        out = tmp_path / "build" / "demo_app"
        out.mkdir(parents=True, exist_ok=True)
        for name in names:
            (out / name).write_bytes(b"data")

    return calls, run


def test_load_dotnet_spec_reads_the_descriptor(tmp_path):
    spec = ar.load_dotnet_spec(write_dotnet_repo(tmp_path, 'publish_profile = "FolderProfile"\n'))

    assert (spec.id, spec.exe, spec.publish_profile) == ("demo_app", "Demo App.exe", "FolderProfile")
    assert spec.project.name == "App.csproj"


def test_load_dotnet_spec_rejects_a_missing_field_or_project(tmp_path):
    repo = write_dotnet_repo(tmp_path)
    (repo / "sct_app.toml").write_text('kind = "dotnet"\nid = "x"\n', encoding="utf-8")
    with pytest.raises(ValueError, match="project"):
        ar.load_dotnet_spec(repo)

    (repo / "sct_app.toml").write_text('kind = "dotnet"\nid = "x"\nproject = "gone.csproj"\nexe = "x.exe"\n', encoding="utf-8")
    with pytest.raises(FileNotFoundError, match="gone.csproj"):
        ar.load_dotnet_spec(repo)


def test_publish_dotnet_runs_msbuild_publish_into_a_clean_folder(tmp_path, monkeypatch):
    spec = ar.load_dotnet_spec(write_dotnet_repo(tmp_path, 'publish_profile = "FolderProfile"\n'))
    calls, run = fake_publish(tmp_path, monkeypatch, ["Demo App.exe"], which="C:/vs/MSBuild.exe")
    monkeypatch.delenv("MSBUILD", raising=False)

    stale = tmp_path / "build" / "demo_app"
    stale.mkdir(parents=True)
    (stale / "stale.txt").write_bytes(b"old")

    out = ar.publish_dotnet(spec, run=run)

    assert out == stale and not (out / "stale.txt").exists()
    command = calls[0]
    assert command[0] == str(Path("C:/vs/MSBuild.exe")) and "-t:Publish" in command and "-p:PublishProfile=FolderProfile" in command
    assert "-clp:ErrorsOnly" in command
    assert any(part.startswith("-p:PublishDir=") for part in command) and not any("Rebuild" in part for part in command)


def test_publish_dotnet_refuses_loose_dependencies(tmp_path, monkeypatch):
    spec = ar.load_dotnet_spec(write_dotnet_repo(tmp_path))
    _, run = fake_publish(tmp_path, monkeypatch, ["Demo App.exe", "Newtonsoft.Json.dll"])

    with pytest.raises(SystemExit, match="single exe"):
        ar.publish_dotnet(spec, run=run)


def test_find_msbuild_exits_without_any_msbuild(monkeypatch, tmp_path):
    monkeypatch.delenv("MSBUILD", raising=False)
    monkeypatch.setattr(ar.shutil, "which", lambda name: None)
    monkeypatch.setenv("ProgramFiles(x86)", str(tmp_path))

    with pytest.raises(SystemExit, match="MSBuild not found"):
        ar.find_msbuild()


def test_find_msbuild_prefers_the_environment_variable_then_path(monkeypatch):
    monkeypatch.setattr(ar.shutil, "which", lambda name: "C:/path/MSBuild.exe")
    monkeypatch.delenv("MSBUILD", raising=False)
    assert ar.find_msbuild() == Path("C:/path/MSBuild.exe")

    monkeypatch.setenv("MSBUILD", "D:/tools/MSBuild.exe")
    assert ar.find_msbuild() == Path("D:/tools/MSBuild.exe")


def run_dotnet_release(tmp_path, monkeypatch, built_version, requested=None):
    repo = write_dotnet_repo(tmp_path)
    monkeypatch.setattr(ar, "REPO_ROOT", tmp_path)
    monkeypatch.setattr(ar, "publish_dotnet", lambda spec: tmp_path / "build" / "demo_app")
    monkeypatch.setattr(ar, "read_exe_version", lambda exe: built_version)
    (tmp_path / "build" / "demo_app").mkdir(parents=True)
    (tmp_path / "build" / "demo_app" / "Demo App.exe").write_bytes(b"MZ")
    ar.cmd_app(SimpleNamespace(module=str(repo), version=requested, upload=False))
    return tmp_path / "release_artifacts"


def test_app_command_releases_a_dotnet_repository_with_the_version_of_the_built_exe(tmp_path, monkeypatch):
    out = run_dotnet_release(tmp_path, monkeypatch, "2.4.1")

    manifest = json.loads((out / "demo_app_2.4.1.json").read_text(encoding="utf-8"))
    assert manifest["product_id"] == "demo_app" and manifest["package_mode"] == "zip"
    assert (out / "demo_app_2.4.1.zip").is_file()


def test_app_command_stops_when_the_requested_version_is_not_the_built_one(tmp_path, monkeypatch):
    with pytest.raises(SystemExit, match="1.4.2"):
        run_dotnet_release(tmp_path, monkeypatch, "1.4.1", requested="1.4.2")


def test_app_command_needs_a_version_for_a_python_module(tmp_path):
    module = write_module(tmp_path, DESCRIPTOR)

    with pytest.raises(SystemExit, match="version"):
        ar.cmd_app(SimpleNamespace(module=str(module), version=None, upload=False))


# --- gc of the server runtime files ---

class RemoteTree:
    """Fake server tree {path: bytes} with the listing, download and delete calls used by gc."""

    def __init__(self, tree):
        self.tree = dict(tree)
        self.files = self

    async def listdir(self, path):
        prefix = path.rstrip("/") + "/"
        kids = {p[len(prefix):]: len(b) for p, b in self.tree.items() if p.startswith(prefix) and "/" not in p[len(prefix):]}
        if not kids:
            raise NextcloudException(404, "Not Found")
        return [SimpleNamespace(name=n, is_dir=False, info=SimpleNamespace(content_length=s)) for n, s in kids.items()]

    async def download(self, path):
        if path not in self.tree:
            raise NextcloudException(404, "Not Found")
        return self.tree[path]

    async def delete(self, path):
        del self.tree[path]


def gc_tree(suite=None, manifests=None):
    def js(value):
        return json.dumps(value).encode()

    root = ar.REMOTE_ROOT
    tree = {
        f"{root}/suite_manifest.json": js(suite or {"products": [{"id": "demo", "latest_version": "2.0.0"}],
                                                  "dev": [{"id": "demo", "latest_version": "2.1.0"}]}),
        f"{ar.WHEELS_REMOTE}/a.whl": b"a", f"{ar.WHEELS_REMOTE}/b.whl": b"b", f"{ar.WHEELS_REMOTE}/old.whl": b"o",
        f"{ar.PYTHON_REMOTE}/py-3.13.5.zip": b"z", f"{ar.PYTHON_REMOTE}/py-3.13.1.zip": b"z",
        f"{root}/runtime/python_3.13.json": js({"file": "py-3.13.5.zip"}),
    }
    for version, wheels in (manifests or {"2.0.0": ["a.whl"], "2.1.0": ["a.whl", "b.whl"]}).items():
        manifest = {"package_mode": "app", "wheels": [{"file": w} for w in wheels]}
        tree[f"{root}/versions/demo/demo_{version}.json"] = js(manifest)
    return tree


def run_gc(tree, delete):
    nc = RemoteTree(tree)

    async def connect():
        return nc

    original = ar.get_nc_client
    ar.get_nc_client = connect
    try:
        ar.cmd_gc(SimpleNamespace(delete=delete))
    finally:
        ar.get_nc_client = original
    return nc


def test_gc_keeps_what_the_products_and_dev_versions_use_and_only_lists_without_delete():
    nc = run_gc(gc_tree(), delete=False)

    assert f"{ar.WHEELS_REMOTE}/old.whl" in nc.tree and f"{ar.PYTHON_REMOTE}/py-3.13.1.zip" in nc.tree


def test_gc_deletes_unused_wheels_and_python_archives():
    nc = run_gc(gc_tree(), delete=True)

    assert f"{ar.WHEELS_REMOTE}/old.whl" not in nc.tree and f"{ar.PYTHON_REMOTE}/py-3.13.1.zip" not in nc.tree
    for kept in (f"{ar.WHEELS_REMOTE}/a.whl", f"{ar.WHEELS_REMOTE}/b.whl", f"{ar.PYTHON_REMOTE}/py-3.13.5.zip"):
        assert kept in nc.tree


def test_gc_refuses_to_judge_when_a_listed_manifest_is_missing():
    tree = gc_tree(manifests={"2.0.0": ["a.whl"]})

    with pytest.raises(SystemExit, match="2.1.0"):
        run_gc(tree, delete=True)


def test_gc_refuses_when_no_app_version_is_listed():
    tree = gc_tree(manifests={"2.0.0": [], "2.1.0": []})

    with pytest.raises(SystemExit, match="No app version"):
        run_gc(tree, delete=True)
