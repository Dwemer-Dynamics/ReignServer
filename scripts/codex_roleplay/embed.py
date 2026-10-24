"""Isolated stdio embedding helper. No service, database, provider, or vector store.

Run with the managed FastEmbed Python environment and an existing read-only model.
Only task-owned input/output pipes are used; model downloads are prohibited.
"""
import argparse
import hashlib
import json
import os
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", required=True, type=Path)
    args = parser.parse_args()
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    from fastembed import TextEmbedding
    model = TextEmbedding("BAAI/bge-small-en-v1.5", threads=1, lazy_load=True,
                          specific_model_path=str(args.model_dir), local_files_only=True)
    metadata = {"model": "BAAI/bge-small-en-v1.5", "dimensions": 384, "threads": 1,
                "files": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in
                          (args.model_dir / "model_optimized.onnx", args.model_dir / "tokenizer.json")}}
    print(json.dumps({"ready": True, "metadata": metadata}), flush=True)
    for line in sys.stdin:
        try:
            texts = json.loads(line)["texts"]
            if not isinstance(texts, list) or not 1 <= len(texts) <= 512:
                raise ValueError("Expected 1..512 texts")
            vectors = [v.tolist() for v in model.embed(texts, batch_size=8)]
            if any(len(v) != 384 for v in vectors):
                raise ValueError("Unexpected embedding dimensions")
            print(json.dumps({"vectors": vectors}), flush=True)
        except Exception as exc:
            print(json.dumps({"error": str(exc)}), flush=True)


if __name__ == "__main__":
    main()
