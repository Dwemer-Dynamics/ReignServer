"""Derive a binary-score-only Qwen ONNX graph from the pinned conversion.

Preserves the pinned Q4 weights, keeps only the last position and no/yes output
columns, retains FP32 floating operations for CPU and converts a separate GPU graph to FP16. The entire derived graph is
qualified separately. No retraining, campaign access or network.
"""
import argparse
import hashlib
import os
from pathlib import Path

TOKENIZER_SHA256 = "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"
SOURCE_SHA256 = "787193952a9aa58374381ec7647b160ac6c200a386dbfc3bc998a3c458081330"
CPU_SHA256 = "a7bd997e927d552a25e2dc2398b86afa6b53b0bd1e5a97d23a7f81bedfdf3da4"
TARGET_SHA256 = "91541a53f5faa6796553aa0d29fea3b24abceccfd45ccfeb78a267d4ea34908a"


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def prepare(directory):
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper
    import onnxruntime
    from onnxruntime.transformers.float16 import convert_float_to_float16
    from tokenizers import Tokenizer
    source = directory / "qwen-reranker.onnx"
    target = directory / "qwen-reranker-binary.onnx"
    if source.is_symlink() or target.is_symlink() or digest(source) != SOURCE_SHA256:
        raise ValueError("Unverified Qwen model source")
    cpu = directory / "qwen-reranker-cpu.onnx"
    if cpu.is_symlink():
        raise ValueError("Unverified CPU model target")
    if target.is_file() and digest(target) == TARGET_SHA256 and cpu.is_file() and digest(cpu) == CPU_SHA256:
        return {"file": target.name, "sha256": TARGET_SHA256, "bytes": target.stat().st_size}
    if onnxruntime.__version__ != "1.22.0":
        raise ValueError("Selection graph preparation requires ONNX Runtime 1.22.0")
    tokenizer_path = directory / "qwen-reranker-tokenizer.json"
    if tokenizer_path.is_symlink() or digest(tokenizer_path) != TOKENIZER_SHA256:
        raise ValueError("Unverified Qwen tokenizer")
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    ids = [tokenizer.token_to_id("no"), tokenizer.token_to_id("yes")]
    if any(i is None for i in ids) or len(set(ids)) != 2:
        raise ValueError("Qwen binary tokens missing")
    model = onnx.load(str(source))
    nodes = list(model.graph.node)
    heads = [n for n in nodes if n.name == "/lm_head/MatMul_Q4" and n.op_type == "MatMulNBits"]
    if len(heads) != 1:
        raise ValueError("Unexpected Qwen output head")
    head = heads[0]
    for name in head.input[1:]:
        weights = next(v for v in model.graph.initializer if v.name == name)
        values = numpy_helper.to_array(weights)
        if values.shape[0] != 151669:
            raise ValueError("Unexpected Qwen output shape")
        model.graph.initializer.remove(weights)
        model.graph.initializer.append(numpy_helper.from_array(values[ids].copy(), name))
    for attribute in head.attribute:
        if attribute.name == "N":
            attribute.i = 2
    last = helper.make_node("Gather", [head.input[0], "reign_last_position"],
        ["reign_last_hidden"], name="reign_last_hidden", axis=1)
    head.input[0] = "reign_last_hidden"
    model.graph.initializer.append(numpy_helper.from_array(np.asarray(-1, dtype=np.int64), "reign_last_position"))
    nodes.insert(nodes.index(head), last)
    del model.graph.node[:]; model.graph.node.extend(nodes)
    del model.graph.output[:]
    model.graph.output.extend([helper.make_tensor_value_info("logits", onnx.TensorProto.FLOAT, ["batch_size", 2])])
    # Stale inferred full-vocabulary shapes must not constrain the derived graph.
    del model.graph.value_info[:]
    onnx.checker.check_model(model)
    # Preserve FP32 floating operations for CPU; the F16 conversion is GPU-only.
    # Expose the existing causal KV tensors for exact, request-scoped prefix
    # continuation. No weight, node, position or attention calculation changes.
    for layer in range(28):
        for kind in ("key", "value"):
            model.graph.output.append(helper.make_tensor_value_info(
                f"present.{layer}.{kind}", onnx.TensorProto.FLOAT,
                ["batch_size", 8, "total_sequence_length", 128]))
    cpu_temporary = cpu.with_suffix(".preparing")
    try:
        onnx.save(model, str(cpu_temporary))
        if digest(cpu_temporary) != CPU_SHA256:
            raise ValueError("Derived CPU graph does not match the validated artifact")
        os.chmod(cpu_temporary, 0o644)
        cpu_temporary.replace(cpu)
    finally:
        cpu_temporary.unlink(missing_ok=True)
    del model.graph.output[1:]
    model = convert_float_to_float16(model, keep_io_types=False)
    temporary = target.with_suffix(".preparing")
    try:
        onnx.save(model, str(temporary))
        if digest(temporary) != TARGET_SHA256:
            raise ValueError("Derived Qwen graph does not match the validated artifact")
        os.chmod(temporary, 0o644)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)
    return {"file": target.name, "sha256": digest(target), "bytes": target.stat().st_size}


if __name__ == "__main__":
    import json
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-directory", required=True, type=Path)
    print(json.dumps(prepare(parser.parse_args().model_directory.resolve())))
