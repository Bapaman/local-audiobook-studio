"""Bounded header/layout operations; no tensor imports or model payload reads."""
import math
import os
import struct
from owned_files import open_file
from protocol import require, keys, integer, json_bytes, canonical_bytes, sha256


def read_main_header(path, expected_bytes):
    with open_file(path) as stream:
        require(os.fstat(stream.fileno()).st_size == integer(expected_bytes, 10, 16 * 1024**3))
        prefix = stream.read(8)
        require(len(prefix) == 8)
        size = struct.unpack("<Q", prefix)[0]
        require(2 <= size <= 1024 * 1024 and 8 + size < expected_bytes)
        raw = stream.read(size)
        require(len(raw) == size)
    header = json_bytes(raw)
    require(type(header) is dict and 1 <= len(header) <= 16384)
    entries, ranges = {}, []
    for name, entry in header.items():
        if name == "__metadata__":
            require(type(entry) is dict and all(type(k) is str and type(v) is str for k, v in entry.items()))
            continue
        require(type(name) is str and 0 < len(name) <= 512 and name.isascii())
        keys(entry, ("dtype", "shape", "data_offsets"))
        require(entry["dtype"] in ("BF16", "F32"))
        shape, offsets = entry["shape"], entry["data_offsets"]
        require(type(shape) is list and len(shape) <= 8 and type(offsets) is list and len(offsets) == 2)
        for dimension in shape:
            integer(dimension, 1, expected_bytes)
        begin, end = (integer(v, 0, expected_bytes) for v in offsets)
        require(end - begin == math.prod(shape) * (2 if entry["dtype"] == "BF16" else 4) and end > begin)
        ranges.append((begin, end))
        entries[name] = entry
    ranges.sort()
    require(ranges and ranges[0][0] == 0 and all(a[1] == b[0] for a, b in zip(ranges, ranges[1:])) and ranges[-1][1] == expected_bytes - 8 - size)
    return entries


def expected_adapter_layout(header):
    result = []
    for name, entry in header.items():
        parts = name.split(".")
        if not name.startswith(("base_lm.", "residual_lm.", "feat_decoder.estimator.")) or len(parts) < 3 or parts[-1] != "weight" or parts[-2] not in ("q_proj", "v_proj", "k_proj", "o_proj"):
            continue
        shape = entry["shape"]
        require(type(shape) is list and len(shape) == 2)
        out_features, in_features = (integer(v, 1, 32768) for v in shape)
        prefix = name.removesuffix("weight")
        result.extend((dict(name=prefix + "lora_A", shape=[2, in_features], dtype="F32"),
                       dict(name=prefix + "lora_B", shape=[out_features, 2], dtype="F32")))
    require(result)
    return sorted(result, key=lambda entry: entry["name"])


def parameter_layout(named_parameters, adapter_names):
    result, cursor = [], 0
    for name, parameter in named_parameters:
        if name not in adapter_names:
            count = math.prod(parameter.shape)
            require(count == parameter.numel())
            result.append([name, list(parameter.shape), cursor, count])
            cursor += count
    # Frozen cache uses source traversal order, unlike adapter optimizer order.
    payload = __import__("json").dumps(result, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    return result, cursor, sha256(payload)
