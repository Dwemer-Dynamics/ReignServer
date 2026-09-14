"""Canonical, offline Windows vector-runtime build. Dependencies are acquired separately."""
from __future__ import annotations
import argparse
import hashlib
import importlib.metadata as metadata
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("REIGN_CANONICAL_RELEASE_BUILD") != "1":
        raise RuntimeError("Use reign_build_release_runtime or reign-validate.ps1 -VectorRuntime; the canonical lease is required.")
    if sys.version_info[:2] != (3, 13) or sys.platform != "win32":
        raise RuntimeError("The release worker requires the locked Python 3.13 Windows x64 build environment.")
    root = Path(__file__).resolve().parent
    worker = root.parent / "ReignBetaServer" / "VectorWorker"
    expected = {}
    for line in (worker / "requirements.lock.txt").read_text(encoding="utf-8-sig").splitlines():
        if line.strip() and not line.startswith("#"):
            name, version = line.split("==", 1)
            expected[name] = version
    for name, version in expected.items():
        if metadata.version(name) != version:
            raise RuntimeError(f"Dependency mismatch: {name}; restore requirements.lock.txt in a separate build environment.")
    model_lock = json.loads((root / "embedding-model.lock.json").read_text(encoding="utf-8-sig"))
    for entry in model_lock["files"]:
        path = args.model / entry["file"]
        if path.stat().st_size != entry["bytes"] or digest(path) != entry["sha256"]:
            raise RuntimeError(f"Embedding-model hash mismatch: {entry['file']}")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    for owned in ("work", "dist", "isolated-proof"):
        if (output / owned).exists():
            raise RuntimeError("Release runtime output must be fresh; prior evidence is retained.")
    command = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--onedir", "--name", "ReignVectorWorker",
               "--workpath", str(output / "work"), "--distpath", str(output / "dist"), "--specpath", str(output),
               "--collect-all", "fastembed", "--collect-all", "qdrant_client", str(worker / "worker.py")]
    with (output / "pyinstaller.log").open("wb") as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=720)
    bundle = output / "dist" / "ReignVectorWorker"
    notices = bundle / "third-party-notices"
    notices.mkdir()
    inventory = []
    for name, version in sorted(expected.items()):
        dist = metadata.distribution(name)
        licenses = []
        for relative in dist.files or []:
            if relative.name.lower().startswith(("license", "notice", "copying")):
                source = Path(dist.locate_file(relative))
                if source.is_file():
                    target = notices / name / str(relative).replace("..", "_").replace(":", "_")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source, target)
                    licenses.append(str(target.relative_to(bundle)))
        inventory.append({"name": name, "version": version, "license": dist.metadata.get("License-Expression") or dist.metadata.get("License", ""), "notices": licenses})
    (notices / "python-packages.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
    # Retain the Python license embedded by PyInstaller where available.
    python_license = Path(sys.base_prefix) / "LICENSE.txt"
    if python_license.is_file():
        shutil.copyfile(python_license, notices / "Python-LICENSE.txt")
    env = os.environ.copy()
    env["REIGN_VECTOR_MODEL_DIR"] = str(args.model.resolve())
    env["HF_HUB_OFFLINE"] = "1"
    env["PYTHONHOME"] = ""
    env["PYTHONPATH"] = ""
    env["PATH"] = os.path.join(os.environ["SystemRoot"], "System32")
    env["TEMP"] = str(output)
    env["TMP"] = str(output)
    with (output / "offline-proof.log").open("wb") as log:
        subprocess.run([str(bundle / "ReignVectorWorker.exe"), "--self-test", "--data-dir", str(output / "isolated-proof")],
                       cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=120)
    files = [{"path": str(path.relative_to(bundle)).replace("\\", "/"), "bytes": path.stat().st_size, "sha256": digest(path)}
             for path in sorted(bundle.rglob("*")) if path.is_file()]
    (output / "bundle-files.json").write_text(json.dumps(files, indent=2), encoding="utf-8")
    inputs = [root / "Build-VectorRuntime.py", root / "embedding-model.lock.json", worker / "worker.py", worker / "requirements.lock.txt"]
    (output / "component-inputs.json").write_text(json.dumps([
        {"path": path.relative_to(root.parent).as_posix(), "bytes": path.stat().st_size, "sha256": digest(path)}
        for path in inputs], indent=2), encoding="utf-8")
    print(json.dumps({"ok": True, "bundle": str(bundle), "files": len(files), "proof": str(output / "isolated-proof" / "release-proof.json")}))


if __name__ == "__main__":
    main()
