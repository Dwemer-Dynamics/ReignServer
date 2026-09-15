"""One-time reviewed source import. Never commits, pushes, or copies Git history.

Run from the retiring workspace only. Destinations must be empty clones of the
two authorized repositories. Report content contains paths/classifiers, no
matching secret text. Ongoing work uses the two checkouts directly.
"""
import argparse
import fnmatch
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess

SERVER_ROOTS = {"ReignBetaServer", "ReignModules", "NativeCharacterImageGenerator", "ReignTools", "ReignRelease"}
PRIVATE_ROOTS = {"ReignBeta", "ReignContent", "ReignMcp", "BannerlordEditorMcp", "docs", "tools", "tests", ".codex"}
ROOT_FILES = {".gitignore", ".gitattributes", "AGENTS.md", "Directory.Build.props", "REIGN_ROADMAP.md", "reign-projects.json", "reign.modules.json", "reign.repository.json", "reign.repositories.json", "reign.testing.json"}
ORIGINS = {name: f"https://github.com/Dwemer-Dynamics/{name}.git" for name in ("Reign", "ReignServer")}


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True).stdout


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def json_write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination-parent", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    source = args.source.resolve()
    destinations = {name: (args.destination_parent / name).resolve() for name in ORIGINS}
    for name, target in destinations.items():
        if target == source or source in target.parents:
            raise ValueError("Launch checkouts must be outside the retiring workspace.")
        if git(target, "remote", "get-url", "origin").decode().strip() != ORIGINS[name]:
            raise ValueError(f"Incorrect destination origin: {name}")
        if git(target, "remote", "get-url", "--push", "origin").decode().strip() != ORIGINS[name]:
            raise ValueError(f"Incorrect push destination: {name}")
        if any(p.name != ".git" for p in target.iterdir()):
            raise ValueError(f"Import destination is not an empty checkout: {name}")
    policy = json.loads((source / "reign.repository.json").read_text(encoding="utf-8-sig"))
    paths = sorted(set(p.decode("utf-8") for p in git(source, "ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0") if p))
    plan, excluded, issues = [], [], []
    for relative in paths:
        path = source / relative
        if not path.is_file():
            continue
        parts = Path(relative).parts
        root = parts[0]
        owner = "ReignServer" if root in SERVER_ROOTS else "Reign" if root in PRIVATE_ROOTS or relative in ROOT_FILES else None
        if owner is None or relative.endswith(".codex/config.toml"):
            excluded.append(relative)
            continue
        if path.is_symlink() or not path.resolve().is_relative_to(source):
            issues.append({"path": relative, "classifier": "redirected-source"})
            continue
        if any(fnmatch.fnmatchcase(relative, pattern) for pattern in policy["prohibitedTrackedPatterns"]):
            issues.append({"path": relative, "classifier": "prohibited-source-path"})
        if any(fnmatch.fnmatchcase(relative, pattern) for pattern in policy["secretNamePatterns"]):
            issues.append({"path": relative, "classifier": "secret-name"})
        if path.suffix.lower() in {".dll", ".exe", ".pdb", ".sav", ".db", ".sqlite", ".zip", ".7z"}:
            issues.append({"path": relative, "classifier": "runtime-or-binary"})
        size = path.stat().st_size
        if size > policy["maxOrdinaryFileBytes"]:
            issues.append({"path": relative, "classifier": "oversized-source"})
        if path.suffix.lower() in set(policy["humanAuthoredExtensions"]) | {".txt", ".js", ".mjs", ".iss"} and size <= policy["maxOrdinaryFileBytes"]:
            content = path.read_text(encoding="utf-8-sig", errors="replace")
            for classifier in policy["secretContentClassifiers"]:
                if classifier["id"] + ":" + relative in policy["secretContentAllowlist"]:
                    continue
                if re.search(classifier["pattern"], content):
                    issues.append({"path": relative, "classifier": classifier["id"]})
        plan.append({"repository": owner, "path": relative, "bytes": size, "sha256": sha(path)})
    report = {"schema": "reign-source-import-review-v1", "sourceHead": git(source, "rev-parse", "HEAD").decode().strip(), "files": plan, "excludedPaths": excluded, "issues": issues, "applied": False}
    json_write(args.report, report)
    if issues:
        print(json.dumps({"ok": False, "report": str(args.report), "issues": issues}, indent=2))
        raise SystemExit(1)
    if args.apply:
        for row in plan:
            target = destinations[row["repository"]] / row["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / row["path"], target)
            if sha(target) != row["sha256"]:
                raise RuntimeError("Copied file hash differs: " + row["path"])
        client, server = destinations["Reign"], destinations["ReignServer"]
        layout = json.loads((client / "reign.repositories.json").read_text(encoding="utf-8-sig"))
        layout["mode"] = "paired"
        json_write(client / "reign.repositories.json", layout)
        private_policy = dict(policy, canonicalRemote=ORIGINS["Reign"])
        private_policy["managedSourceRoots"] = sorted(PRIVATE_ROOTS)
        private_policy["requiredTrackedPaths"] = list(dict.fromkeys(policy["requiredTrackedPaths"] + ["reign.repositories.json", "Directory.Build.props"]))
        json_write(client / "reign.repository.json", private_policy)
        ignore = (source / ".gitignore").read_text(encoding="utf-8-sig")
        (client / ".gitignore").write_text(ignore + "\n# Server-owned junctions; commit changes in ../ReignServer.\n" + "".join(f"/{root}/\n" for root in sorted(SERVER_ROOTS)), encoding="utf-8")
        server_policy = dict(policy, canonicalRemote=ORIGINS["ReignServer"], managedSourceRoots=sorted(SERVER_ROOTS), requiredTrackedPaths=[".gitignore", ".gitattributes", "AGENTS.md", "README.md", "reign.repository.json", "ReignRelease/release.json", "ReignBetaServer/ReignBetaServer.csproj"])
        server_policy["prohibitedTrackedPatterns"] = policy["prohibitedTrackedPatterns"] + ["ReignBeta/**", "ReignMcp/**", "ReignRelease/payloads/**", "ReignRelease/output/**"]
        json_write(server / "reign.repository.json", server_policy)
        (server / ".gitignore").write_text(ignore + "\n/ReignRelease/payloads/\n/ReignRelease/output/\n/ReignBeta/\n/ReignMcp/\n", encoding="utf-8")
        shutil.copy2(source / ".gitattributes", server / ".gitattributes")
        (server / "AGENTS.md").write_text("# ReignServer local workflow\n\nThis public repository owns the local Reign server, shared contracts/helpers, native portrait tooling and release packaging. Its only publishing destination is https://github.com/Dwemer-Dynamics/ReignServer.git, whose default branch is `reign`; do not restore or target the deleted `main` branch. Keep `dev` and `unstable` synchronized with `reign` when publishing a user-approved source sync across all three channels. The complete approved shipped portrait library belongs in the paired private Reign repository under `ReignContent/PortraitCache/_shared`, never in this public repository or only in a runtime cache. Release packaging must reject a machine-local or untracked first-party portrait inventory. Private client source, credentials, campaigns, verification workspaces, built binaries and independently locked third-party model/runtime downloads do not belong in this public Git repository.\n\nFor paired development, clone the private Reign repository beside this checkout and run ReignRelease/Connect-Repositories.ps1. Follow ../Reign/AGENTS.md and obtain the Reign MCP validation plan before code changes. Use the shared canonical validation engine and its OS lease; never start a second server or bypass provider/campaign gates. All development and testing stay local. Preserve source/save identities and edit shared source here only.\n\nUse the Project Memory skill for substantive work and register this exact checkout independently. Stage explicit reviewed paths only. Validate and audit both repositories before publication. Initial uploading is held until the complete package has clean-install execution evidence on another computer.\n", encoding="utf-8")
        (server / "README.md").write_text("# ReignServer\n\nReignServer runs locally beside Bannerlord Reign. This public repository contains the server, shared contracts, native portrait generator, vector worker and release packaging sources. The game module is maintained in [Reign](https://github.com/Dwemer-Dynamics/Reign). Internal ReignBeta names remain for save compatibility.\n\nFor local development, use sibling checkouts named Reign and ReignServer. Run `ReignRelease/Connect-Repositories.ps1`, then use the canonical Reign MCP validator from Reign. The source junctions keep integration tooling working while every file has one Git owner. Commit server changes here and client changes in Reign.\n\nInstallation packages combine this public server source with private client content and independently locked third-party runtime/model payloads. The complete approved shared portrait library is authoritative source in the private Reign repository under `ReignContent/PortraitCache/_shared`; packaging rejects machine-local or untracked portrait inputs. Each user supplies their own AI credentials.\n", encoding="utf-8")
        report["applied"] = True
        report["destinations"] = {name: str(path) for name, path in destinations.items()}
        report["generatedPaths"] = {"Reign": [".gitignore", "reign.repository.json", "reign.repositories.json"], "ReignServer": [".gitignore", ".gitattributes", "reign.repository.json", "AGENTS.md", "README.md"]}
        json_write(args.report, report)
    print(json.dumps({"ok": True, "files": len(plan), "bytes": sum(p["bytes"] for p in plan), "excluded": len(excluded), "applied": args.apply, "report": str(args.report)}))


if __name__ == "__main__":
    main()
