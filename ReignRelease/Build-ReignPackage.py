"""Assemble an offline Reign installer from canonical validated artifacts and locked downloads."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import zipfile
import xml.etree.ElementTree as ET

TICKS_EPOCH = 621355968000000000
FORBIDDEN = {".git", "verification_contracts", "data", "save-sync", "deployment-backups", "obj"}

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")

def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def verify(path, entry):
    if not path.is_file() or path.stat().st_size != entry["bytes"] or digest(path) != entry["sha256"]:
        raise ValueError(f"Locked input failed verification: {path.name}")

def safe_relative(name):
    p = PurePosixPath(name)
    if p.is_absolute() or "\\" in name or not name or any(part in ("", ".", "..") or ":" in part for part in name.split("/")):
        raise ValueError("Unsafe archive path")
    return p

def copy(source, target):
    source, target = Path(source), Path(target)
    if source.is_symlink() or not source.is_file():
        raise ValueError(f"Missing or linked source: {source.name}")
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)

def tree(source, target, allow=lambda p: True):
    source, target = Path(source), Path(target)
    if not source.is_dir():
        raise ValueError(f"Missing runtime directory: {source.name}")
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ValueError("Runtime source contains a link")
        if path.is_file():
            relative = path.relative_to(source)
            if allow(relative):
                copy(path, target / relative)

def inventory(root):
    return [{"path": p.relative_to(root).as_posix(), "bytes": p.stat().st_size, "sha256": digest(p),
             "lastWriteUtcTicks": p.stat().st_mtime_ns // 100 + TICKS_EPOCH}
            for p in sorted(root.rglob("*")) if p.is_file()]

def archive(root, output, identity, version):
    files = inventory(root)
    if identity != "portraits":
        for item in files:
            item["lastWriteUtcTicks"] = 637134336000000000  # 2020-01-01 UTC; no runtime timestamp semantics.
    index = {"schema": "reign-payload-v1", "id": identity, "version": version, "files": files}
    name = f"Reign-{identity}-{version}.zip"
    target = output / name
    with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=3, allowZip64=True) as bundle:
        header = zipfile.ZipInfo("payload.json", (2020, 1, 1, 0, 0, 0))
        header.compress_type = zipfile.ZIP_DEFLATED
        bundle.writestr(header, json.dumps(index, ensure_ascii=False).encode("utf-8"))
        for item in files:
            path = root / item["path"]
            # PNG/video/model/executable assets are already compressed or have
            # limited gains; avoid recompressing multi-gigabyte portrait masters.
            method = zipfile.ZIP_STORED if path.suffix.lower() in (".png", ".mp4", ".zip", ".7z") else zipfile.ZIP_DEFLATED
            info = zipfile.ZipInfo(item["path"], (2020, 1, 1, 0, 0, 0))
            info.compress_type = method
            info.external_attr = 0o100644 << 16
            with path.open("rb") as source, bundle.open(info, "w", force_zip64=True) as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
    return {"id": identity, "version": version, "file": name, "bytes": target.stat().st_size,
            "sha256": digest(target), "expandedBytes": sum(f["bytes"] for f in files), "fileCount": len(files)}

def portable_metadata(value):
    if isinstance(value, dict):
        return {k: portable_metadata(v) for k, v in value.items()}
    if isinstance(value, list):
        return [portable_metadata(v) for v in value]
    if isinstance(value, str) and re.match(r"^[A-Za-z]:[\\/]", value):
        return value.replace("\\", "/").rsplit("/", 1)[-1]
    return value

def audit_payload(root):
    failures = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        parts = path.relative_to(root).parts
        if any(p.lower() in FORBIDDEN for p in parts) or path.suffix.lower() in (".pdb", ".dump", ".db", ".sav", ".log", ".cs", ".csproj"):
            failures.append(path.relative_to(root).as_posix())
        if path.name.lower().startswith("taleworlds.") or path.name.lower() in ("bannerlord.exe", "steam_api64.dll"):
            failures.append(path.relative_to(root).as_posix())
        if path.suffix.lower() in (".json", ".config", ".ps1", ".cmd", ".txt", ".xml") and path.stat().st_size < 4 * 1024 * 1024:
            text = path.read_text(encoding="utf-8-sig", errors="replace")
            if re.search(r"\b(?:sk-[A-Za-z0-9_-]{24,}|gh[pousr]_[A-Za-z0-9]{30,})\b", text) or re.search(r'"(?:apiKey|access_token|refresh_token|password)"\s*:\s*"[^"\s]{8,}"', text, re.I):
                failures.append(path.relative_to(root).as_posix())
    if failures:
        # Paths only: never include matching secret text.
        raise ValueError("Prohibited release files or possible credentials: " + ", ".join(sorted(set(failures))[:25]))

def tracked_private_portrait_source(workspace, inventory_path, portrait_inventory):
    expected_inventory = (workspace / "ReignContent" / "shared-portrait-inventory.json").resolve()
    if inventory_path.resolve() != expected_inventory:
        raise ValueError("Shared portraits must use the tracked private ReignContent inventory")
    if portrait_inventory.get("schema") != "reign-shared-content-inventory-v1":
        raise ValueError("Unsupported shared portrait inventory")
    configured_root = Path(portrait_inventory["root"])
    if configured_root.is_absolute():
        raise ValueError("Shared portrait inventory root must be repository-relative")
    portrait_source = (inventory_path.parent / configured_root).resolve()
    expected_source = (workspace / "ReignContent" / "PortraitCache" / "_shared").resolve()
    if portrait_source != expected_source:
        raise ValueError("Shared portrait source must be ReignContent/PortraitCache/_shared")
    tracked = set(subprocess.run(
        ["git", "-C", str(workspace), "ls-files", "-z", "--", "ReignContent"],
        check=True, capture_output=True).stdout.decode("utf-8").rstrip("\0").split("\0"))
    required = {"ReignContent/shared-portrait-inventory.json"}
    required.update("ReignContent/PortraitCache/_shared/" + entry["path"] for entry in portrait_inventory["files"])
    missing = sorted(required - tracked)
    if missing:
        raise ValueError("Shared portrait source contains untracked package inputs: " + ", ".join(missing[:10]))
    return portrait_source

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--spec", type=Path, required=True)
    parser.add_argument("--validation-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("REIGN_CANONICAL_RELEASE_BUILD") != "1" or sys.platform != "win32":
        raise RuntimeError("Use reign_build_release_package / reign-validate.ps1 -ReleasePackage under the canonical lease.")
    spec, report = read(args.spec), read(args.validation_report)
    if spec["schema"] != "reign-package-inputs-v1" or not report["Ok"] or report["Plan"]["Profile"] != "all":
        raise ValueError("Release packaging requires a successful all-profile validation report.")
    release_root = Path(__file__).resolve().parent
    server_repo = release_root.parent
    workspace = Path(os.environ["REIGN_WORKSPACE_ROOT"])
    release = read(release_root / "release.json")
    output = args.output.resolve()
    staging = output / "staging"
    staging.mkdir(parents=True, exist_ok=False)
    product = output / "package"
    product.mkdir()
    components = {name: staging / name for name in ("client", "server", "runtime", "dependencies", "portraits")}
    for root in components.values():
        root.mkdir()
    downloads = Path(spec["downloadDirectory"])
    dependencies = read(release_root / "dependencies.lock.json")
    for entry in dependencies["artifacts"]:
        verify(downloads / entry["file"], entry)
    validated = Path(report["ArtifactRoot"])
    client_bin, server_bin = validated / "client" / "out", validated / "server" / "out"
    if not (client_bin / "ReignBeta.dll").is_file() or not (server_bin / "ReignBetaServer.exe").is_file():
        raise ValueError("Canonical runtime artifacts are incomplete")
    module = components["client"] / "Modules" / "ReignBeta"
    for directory in ("GUI", "ModuleData", "EventArt", "TavernArt", "Videos"):
        tree(workspace / "ReignBeta" / directory, module / directory)
    copy(workspace / "ReignBeta" / "SubModule.xml", module / "SubModule.xml")
    for path in client_bin.iterdir():
        if path.suffix.lower() == ".dll" and not path.name.startswith("TaleWorlds."):
            copy(path, module / "bin" / "Win64_Shipping_Client" / path.name)
    app = components["server"] / "app"
    tree(server_bin, app, lambda p: (len(p.parts) == 1 and (p.suffix.lower() in (".dll", ".exe", ".config") or p.name.endswith(".deps.json")))
         or p.parts[0] in ("assets", "ProfileLibrary", "portrait_models", "native-portrait-generator") and p.suffix.lower() not in (".pdb", ".lib"))
    identity = {"schema": "reign-release-identity-v1", "version": release["version"], "protocolVersion": release["protocolVersion"], "sourceFingerprint": report["SourceFingerprintSha256"]}
    write(app / "release.json", identity)
    write(module / "release.json", identity)
    for target in (module, components["server"]):
        copy(release_root / "Start-ReignServer.ps1", target / "Start-ReignServer.ps1")
        copy(workspace / "ReignBeta" / "Start ReignBeta Server.cmd", target / "Start ReignBeta Server.cmd")
    runtime = components["runtime"] / "runtime"
    pg_entry = next(e for e in dependencies["artifacts"] if e["id"] == "postgresql")
    with zipfile.ZipFile(downloads / pg_entry["file"]) as pg:
        for item in pg.infolist():
            p = safe_relative(item.filename.rstrip("/"))
            if item.is_dir() or p.parts[0] != "pgsql":
                continue
            relative = PurePosixPath(*p.parts[1:])
            if relative.parts[0] not in ("bin", "lib", "share") and relative.name not in ("server_license.txt", "commandlinetools_3rd_party_licenses.txt"):
                continue
            target = runtime / "postgresql" / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with pg.open(item) as src, target.open("wb") as dst:
                shutil.copyfileobj(src, dst)
    codex_entry = next(e for e in dependencies["artifacts"] if e["id"] == "codex")
    with tarfile.open(downloads / codex_entry["file"], "r:gz") as codex:
        for item in codex:
            if not item.isfile():
                continue
            relative = safe_relative(item.name.removeprefix("./"))
            if relative.parts[0] not in ("bin", "codex-path", "codex-resources", "codex-package.json"):
                raise ValueError("Unexpected official Codex package layout")
            target = runtime / "codex" / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with codex.extractfile(item) as src, target.open("wb") as dst:
                shutil.copyfileobj(src, dst)
    vector_report_path = Path(spec["vectorRuntimeReport"])
    vector_report = read(vector_report_path)
    if not vector_report["Ok"]:
        raise ValueError("Vector component proof did not pass")
    vector_root = vector_report_path.parent
    for item in read(vector_root / "component-inputs.json"):
        verify(server_repo / item["path"], item)
    for entry in read(vector_root / "bundle-files.json"):
        verify(vector_root / "dist" / "ReignVectorWorker" / entry["path"], entry)
    tree(vector_root / "dist" / "ReignVectorWorker", runtime / "vector-worker")
    for entry in read(release_root / "embedding-model.lock.json")["files"]:
        source = Path(spec["modelDirectory"]) / entry["file"]
        verify(source, entry)
        copy(source, components["runtime"] / "models" / "embeddings" / entry["file"])
    copy(downloads / "vc_redist.x64.exe", runtime / "prerequisites" / "vc_redist.x64.exe")
    for entry in dependencies["artifacts"]:
        if entry["role"] != "bannerlord-module":
            continue
        extracted = output / "dependency-inputs" / entry["id"]
        extracted.mkdir(parents=True, exist_ok=False)
        subprocess.run(["tar.exe", "-xf", str(downloads / entry["file"]), "-C", str(extracted)], check=True, timeout=60)
        # Upstream releases also contain debugging symbols and Game Pass builds.
        # This package supports the Steam Win64 module layout only.
        tree(extracted, components["dependencies"], lambda p:
             len(p.parts) >= 3 and p.parts[:2] == ("Modules", entry["id"])
             and (p.parts[2] != "bin" or len(p.parts) >= 5 and p.parts[3] == "Win64_Shipping_Client")
             and p.suffix.lower() not in (".pdb", ".lib", ".cs", ".csproj", ".sln"))
    notices = runtime / "third-party-notices"
    for notice in read(release_root / "ThirdParty" / "provenance.json"):
        path = release_root / "ThirdParty" / safe_relative(notice["file"])
        if digest(path) != notice["sha256"]:
            raise ValueError("Third-party notice hash mismatch: " + notice["file"])
    tree(release_root / "ThirdParty", notices)
    copy(downloads / "Bannerlord.UIExtenderEx-v2.13.2-source.zip", notices / "corresponding-source" / "Bannerlord.UIExtenderEx-v2.13.2-source.zip")
    # NuGet packages used in the validated runtime: retain their package license
    # declarations and packaged notices, without copying developer package caches.
    nuget = Path(spec["nugetDirectory"])
    used = set()
    native_assets = server_repo / "NativeCharacterImageGenerator" / "src" / "NativeCharacterImageGenerator.App" / "obj" / "project.assets.json"
    for deps in (app / "ReignBetaServer.deps.json", workspace / "ReignBeta" / "obj" / "project.assets.json", native_assets):
        if deps.is_file():
            for name, value in read(deps).get("libraries", {}).items():
                if value.get("type") == "package":
                    used.add(name.lower())
            for framework in read(deps).get("project", {}).get("frameworks", {}).values():
                for item in framework.get("downloadDependencies", []):
                    if ".runtime." in item["name"].lower():
                        used.add(item["name"].lower() + "/" + item["version"].strip("[]").split(",")[0].strip())
    packages = []
    for name in sorted(used):
        directory = nuget / name
        if not directory.is_dir():
            raise ValueError("Missing NuGet license provenance: " + name)
        nuspec = next(directory.glob("*.nuspec"))
        metadata = ET.parse(nuspec).getroot()
        license_text = [(element.tag.split("}")[-1], element.text or "", element.attrib) for element in metadata.iter() if element.tag.split("}")[-1] in ("license", "licenseUrl", "projectUrl", "copyright")]
        packages.append({"package": name, "provenance": license_text})
        for path in directory.rglob("*"):
            if path.is_file() and path.name.lower().startswith(("license", "notice", "copying")) and path.stat().st_size < 2 * 1024 * 1024:
                copy(path, notices / "nuget" / name / path.relative_to(directory))
    write(notices / "nuget-packages.json", packages)
    portrait_inventory_path = Path(spec["portraitInventory"])
    portrait_inventory = read(portrait_inventory_path)
    portrait_source = tracked_private_portrait_source(workspace, portrait_inventory_path, portrait_inventory)
    allowed = {"portrait.png", "portrait_chest.png", "thumbnail_wide.png", "thumbnail.png", "zoom.png", "portrait_input.json", ".portrait_derivatives.json", ".ai_generation.json", "formal_outfit.json", "prompt.txt"}
    listed_paths = [safe_relative(entry["path"]) for entry in portrait_inventory["files"]]
    if len(set(listed_paths)) != len(listed_paths):
        raise ValueError("Shared portrait inventory contains duplicate paths")
    actual_paths = {path.relative_to(portrait_source).as_posix() for path in portrait_source.rglob("*") if path.is_file()}
    if actual_paths != {path.as_posix() for path in listed_paths}:
        raise ValueError("Tracked shared portrait source and inventory differ")
    for entry in portrait_inventory["files"]:
        relative = safe_relative(entry["path"])
        if len(relative.parts) != 2 or relative.name not in allowed:
            raise ValueError("Shared portrait inventory is outside the runtime allowlist")
        source = portrait_source / relative
        verify(source, entry)
        target = components["portraits"] / "PortraitCache" / "_shared" / relative
        copy(source, target)
        if target.suffix.lower() == ".json":
            write(target, portable_metadata(read(target)))
        ticks = int(entry["lastWriteUtcTicks"])
        os.utime(target, ns=((ticks - TICKS_EPOCH) * 100, (ticks - TICKS_EPOCH) * 100))
    for name, root in components.items():
        print(f"Auditing and packaging {name}...", flush=True)
        audit_payload(root)
    versions = {"portraits": release["contentVersion"], "runtime": release["runtimeVersion"], "dependencies": release["dependenciesVersion"]}
    payloads = [archive(root, product, name, versions.get(name, release["version"])) for name, root in components.items()]
    package = {"schema": "reign-package-v1", "version": release["version"], "releaseSequence": release["releaseSequence"], "protocolVersion": release["protocolVersion"], "contentVersion": release["contentVersion"], "sourceFingerprint": report["SourceFingerprintSha256"], "payloads": payloads}
    write(product / "package.json", package)
    tree(release_root, product / "bootstrap", lambda p: len(p.parts) == 1 and p.suffix == ".ps1" and p.name in ("Reign-Installer.ps1", "Install-Reign.ps1", "Uninstall-Reign.ps1", "Initialize-PostgreSql.ps1", "Start-ReignServer.ps1"))
    copy(release_root / "INSTALL.txt", product / "INSTALL.txt")
    compiler = Path(spec["innoCompiler"])
    with (output / "installer-compiler.log").open("wb") as log:
        subprocess.run([str(compiler), "/DPackageRoot=" + str(product), "/DReleaseVersion=" + release["version"], "/DReleaseNumericVersion=" + release["version"].split("-")[0] + ".0", str(release_root / "ReignSetup.iss")], stdout=log, stderr=subprocess.STDOUT, check=True, timeout=180)
    setup = product / ("ReignSetup-" + release["version"] + ".exe")
    proof = {"schema": "reign-package-assembly-v1", "ok": setup.is_file(), "version": release["version"], "sourceFingerprint": report["SourceFingerprintSha256"], "validationRunId": report["RunId"], "setup": {"file": setup.name, "bytes": setup.stat().st_size, "sha256": digest(setup)}, "payloads": payloads, "cleanComputerAccepted": False, "nativeBannerlordAccepted": False, "providerCallsMade": False}
    write(output / "assembly-proof.json", proof)
    (product / "SHA256SUMS.txt").write_text("\n".join(f"{digest(p)}  {p.name}" for p in sorted(product.iterdir()) if p.is_file()), encoding="utf-8")
    print(json.dumps({"ok": True, "packageDirectory": str(product), "payloadBytes": sum(p["bytes"] for p in payloads)}))

if __name__ == "__main__":
    main()
