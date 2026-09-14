# Reign Vector Worker

Managed local semantic-memory worker using FastEmbed's ONNX `BAAI/bge-small-en-v1.5` model and persistent local Qdrant or an explicitly configured external Qdrant service.

Release builds include the model. The installed launcher sets `REIGN_VECTOR_MODEL_DIR`; that mode requires complete local files and never downloads a replacement. Development without that variable retains the original download cache behavior.

Restore the complete pinned `requirements.lock.txt` into an isolated Windows x64 Python 3.13 build environment. Package through `reign_build_release_runtime` or the paired workspace's `ReignMcp/scripts/reign-validate.ps1 -VectorRuntime`. The build records hashes and third-party notices and runs the bundled executable's offline inference/persistence proof. End users do not need Python or a compiler. See `ReignRelease/embedding-model.lock.json` for exact model provenance and hashes.