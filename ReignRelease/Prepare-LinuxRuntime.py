"""Acquire the existing pinned embedding model without touching campaigns or credentials."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import tempfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-directory", required=True, type=Path)
    args = parser.parse_args()
    root = args.model_directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(Path(__file__).with_name("embedding-model.lock.json").read_text())
    for entry in manifest["files"]:
        name = entry["file"]
        if Path(name).name != name:
            raise ValueError("Model manifest contains a nonlocal filename")
        destination = root / name
        if destination.is_symlink():
            raise ValueError("Model destination must not be a symbolic link")
        if destination.is_file() and hashlib.sha256(destination.read_bytes()).hexdigest() == entry["sha256"]:
            continue
        with urllib.request.urlopen(entry["url"], timeout=120) as response:
            payload = response.read(entry["bytes"] + 1)
        if len(payload) != entry["bytes"] or hashlib.sha256(payload).hexdigest() != entry["sha256"]:
            raise ValueError(f"Model checksum mismatch: {name}")
        with tempfile.NamedTemporaryFile(dir=root, prefix="." + name + ".", suffix=".download", delete=False) as output:
            temporary = Path(output.name)
            output.write(payload)
        try:
            temporary.chmod(0o644)
            temporary.replace(destination)
        finally:
            temporary.unlink(missing_ok=True)
    print("Pinned Reign embedding model verified.")


if __name__ == "__main__":
    main()
