"""Explicit offline FP32 candidate export. Runtime never exports or downloads."""
from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

from claim_validation import (GROUNDING_HASH, GROUNDING_WEIGHTS_HASH,
                              GROUNDING_SOURCE_HASH, GROUNDING_TOKENIZER_HASH,
                              file_hash)

SOURCE_CONFIG_HASH = "8a8eabb5f1bc47c9f221486964d208064f6649eafd6cf992020a200a4e1a4f8d"


def check_source(source):
    for name, expected in (("pytorch_model.bin", GROUNDING_SOURCE_HASH),
                           ("tokenizer.json", GROUNDING_TOKENIZER_HASH),
                           ("config.json", SOURCE_CONFIG_HASH),
                           ("tokenizer_config.json", "c5d20c69691d05875de7ac9a8cb735c8cd89499114ef0071dd51c2a17fb20278"),
                           ("special_tokens_map.json", "df07ec76585071dc1ffa857ab911880a32c7818d87f7b7fad0142e545dd6d1e5"),
                           ("added_tokens.json", "1dbe7c2023658ad369d0e79ea05048aa0b845b518a1334fa147b5b1904d9b600"),
                           ("spm.model", "c679fbf93643d19aab7ee10c0b99e460bdbc02fedf34b92b05af343b4af586fd")):
        path = source / name
        if path.is_symlink() or not path.is_file() or file_hash(path) != expected:
            raise ValueError("unqualified_source_file: " + name)
    config = json.loads((source / "config.json").read_text(encoding="utf-8"))
    if config.get("auto_map") or config.get("model_type") != "deberta-v2":
        raise ValueError("unsupported_source_architecture")


def prepare(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    check_source(source)
    if source == output or source in output.parents or output in source.parents:
        raise ValueError("source_and_output_must_be_separate")
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError("output_must_be_fresh_and_empty")
    output.mkdir(parents=True, exist_ok=True)
    # These dependencies are for explicit development export only. The deployed
    # worker uses existing ONNX Runtime/tokenizers; it does not need PyTorch.
    import numpy as np
    import onnx
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    torch.set_num_threads(2)
    model = AutoModelForSequenceClassification.from_pretrained(
        source, local_files_only=True, trust_remote_code=False, weights_only=True).to("cpu").eval()
    tokenizer = AutoTokenizer.from_pretrained(source, local_files_only=True, trust_remote_code=False)

    class Wrapper(torch.nn.Module):
        def __init__(self, model):
            super().__init__()
            self.model = model

        def forward(self, input_ids, attention_mask):
            return self.model(input_ids=input_ids, attention_mask=attention_mask).logits

    wrapper = Wrapper(model)
    encoded = tokenizer("person B said stop talking about the future." + tokenizer.eos_token
                        + "person A said stop talking about the future.", return_tensors="pt")
    graph_path = output / "dialogue-minicheck.onnx"
    with torch.inference_mode():
        torch.onnx.export(wrapper, (encoded["input_ids"], encoded["attention_mask"]), str(graph_path),
            input_names=["input_ids", "attention_mask"], output_names=["logits"], export_params=False,
            do_constant_folding=False, dynamo=False, opset_version=17,
            dynamic_axes={"input_ids": {0: "batch", 1: "sequence"},
                          "attention_mask": {0: "batch", 1: "sequence"}, "logits": {0: "batch"}})
    graph = onnx.load(str(graph_path), load_external_data=False)
    parameters = dict(wrapper.named_parameters())
    parameters.update(dict(wrapper.named_buffers()))
    offset = 0
    with (output / "dialogue-minicheck.weights").open("wb") as stream:
        for entry in list(graph.graph.input):
            if entry.name in ("input_ids", "attention_mask"):
                continue
            if entry.name not in parameters:
                raise ValueError("unknown_export_parameter")
            tensor = parameters[entry.name].detach().contiguous().numpy()
            if tensor.dtype != np.float32:
                raise ValueError("unexpected_parameter_dtype")
            stream.write(memoryview(tensor).cast("B"))
            proto = onnx.TensorProto()
            proto.name, proto.data_type = entry.name, onnx.TensorProto.FLOAT
            proto.dims.extend(tensor.shape)
            proto.data_location = onnx.TensorProto.EXTERNAL
            for key, value in (("location", "dialogue-minicheck.weights"),
                               ("offset", str(offset)), ("length", str(tensor.nbytes))):
                field = proto.external_data.add()
                field.key, field.value = key, value
            graph.graph.initializer.append(proto)
            graph.graph.input.remove(entry)
            offset += tensor.nbytes
    onnx.save(graph, str(graph_path))
    shutil.copyfile(source / "tokenizer.json", output / "dialogue-minicheck-tokenizer.json")
    for name, expected in (("dialogue-minicheck.onnx", GROUNDING_HASH),
                           ("dialogue-minicheck.weights", GROUNDING_WEIGHTS_HASH),
                           ("dialogue-minicheck-tokenizer.json", GROUNDING_TOKENIZER_HASH)):
        if file_hash(output / name) != expected:
            raise ValueError("export_fingerprint_mismatch: " + name)
    return {"schema": "reign-dialogue-grounding-prepare-v1", "ok": True,
            "modelHash": GROUNDING_HASH, "weightsHash": GROUNDING_WEIGHTS_HASH,
            "tokenizerHash": GROUNDING_TOKENIZER_HASH, "remoteCalls": 0,
            "semanticAccuracyCertified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-directory", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(prepare(args.source_directory, args.output_directory)))


if __name__ == "__main__":
    main()
