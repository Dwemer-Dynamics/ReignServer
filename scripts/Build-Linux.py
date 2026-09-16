"""Canonical standalone Linux server build for DwemerDistro's managed source updates."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    source = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    if output == source or source.is_relative_to(output) or output.is_relative_to(source):
        raise ValueError("Build output must be separate from the source checkout")
    if output.exists():
        raise ValueError("Use a fresh output directory")
    output.mkdir(parents=True)
    # Distro updates hold ddistro_server's operation lock; this also serializes direct source builds.
    import fcntl
    git_dir = Path(subprocess.check_output(["git", "-C", str(source), "rev-parse", "--absolute-git-dir"], text=True).strip())
    with (git_dir / "reign-linux-build.lock").open("w") as lease:
        fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        before = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
        subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "--quiet", "HEAD", "--"], check=True)
        if subprocess.check_output(["git", "-C", str(source), "ls-files", "--others", "--exclude-standard"]):
            raise RuntimeError("Public source updates require a clean checkout; use the paired validator for local development")
        subprocess.run(["dotnet", "publish", str(source / "ReignServer.csproj"),
                        "-c", "Release", "--runtime", "linux-x64",
                        "--self-contained", "true", "--output", str(output)], check=True)
        after = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
        if before != after:
            raise RuntimeError("Source revision changed during build")
        subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "--quiet", "HEAD", "--"], check=True)
        if subprocess.check_output(["git", "-C", str(source), "ls-files", "--others", "--exclude-standard"]):
            raise RuntimeError("Untracked source appeared during build")
        release = json.loads((source / "release/release.json").read_text())
        files = {p.relative_to(output).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                 for p in output.rglob("*") if p.is_file()}
        (output / "reign-linux-artifact.json").write_text(json.dumps({
            "schema": "reign-linux-artifact-v1", "version": release["version"],
            "protocolVersion": release["protocolVersion"], "sourceCommit": before, "files": files
        }, indent=2))
        os.chmod(output / "ReignServer", 0o755)


if __name__ == "__main__":
    main()
