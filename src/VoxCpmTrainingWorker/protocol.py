"""Strict, bounded Format1 training protocol; stdlib only."""
import hashlib
import json
import math
import os
from pathlib import Path
import re
from owned_files import ProtocolError, require, io_path, open_file

REQUEST_LIMIT = 256 * 1024
MANIFEST_LIMIT = 64 * 1024
RESULT_LIMIT = 1024 * 1024
PAYLOAD_LIMITS = {"lora_weights.safetensors": 8 * 1024 * 1024, "optimizer.pth": 16 * 1024 * 1024,
                  "scheduler.pth": 64 * 1024, "rng.pth": 64 * 1024, "metadata.json": 256 * 1024}
ID_KEYS = ("requestId", "projectId", "trainingId", "actionId", "executionId", "windowId", "checkpointId")
CORPUS_KEYS = ("corpusVersionId", "corpusManifestSha256", "corpusBindingSha256")
IDENTITY_KEYS = {"fingerprint", "sourceCommit", "baseRevision", "sourceLockSha256", "modelLockSha256", "cacheSha256",
                 "cacheLayoutSha256", "loaderSha256", "recipeSha256", "adapterLayoutSha256"}


def keys(value, expected):
    require(type(value) is dict and set(value) == set(expected))


def integer(value, minimum=0, maximum=2**53 - 1):
    require(type(value) is int and minimum <= value <= maximum)
    return value


def digest(value, length=64):
    require(type(value) is str and re.fullmatch("[0-9a-f]{" + str(length) + "}", value) is not None)
    return value


def identity(value):
    return digest(value, 32)


def finite(value, minimum=-1e100, maximum=1e100):
    require(type(value) in (int, float) and math.isfinite(value) and minimum <= value <= maximum)
    return value


def canonical_bytes(value):
    try:
        return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    except (ValueError, TypeError, UnicodeError, RecursionError) as error:
        raise ProtocolError() from error


def sha256(payload):
    return hashlib.sha256(payload).hexdigest()


def relative_parts(relative):
    require(type(relative) is str and 0 < len(relative) <= 2048 and not any(c in relative for c in '\\:<>"|?*'))
    parts = relative.split("/")
    reserved = {"CON", "PRN", "AUX", "NUL", *("COM" + str(i) for i in range(1, 10)), *("LPT" + str(i) for i in range(1, 10))}
    require(all(p not in ("", ".", "..") and p[-1] not in (".", " ") and p.split(".")[0].upper() not in reserved and not any(ord(c) < 32 for c in p) for p in parts))
    return parts


def safe_path(root, relative):
    return io_path(root).joinpath(*relative_parts(relative))


def read_bytes(path, limit):
    with open_file(path) as stream:
        require(os.fstat(stream.fileno()).st_size <= limit)
        value = stream.read(limit + 1)
    require(len(value) <= limit)
    return value


def json_bytes(payload):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result
    def number(text):
        require(re.fullmatch(r"0|-?[1-9][0-9]*", text) is not None)
        require(len(text) <= 21)
        return int(text)
    def floating(text):
        result = float(text)
        require(math.isfinite(result))
        return result
    def constant(_):
        raise ProtocolError()
    try:
        return json.loads(payload.decode("utf-8"), object_pairs_hook=pairs, parse_int=number, parse_float=floating, parse_constant=constant)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ProtocolError() from error


def read_json(path, expected_sha=None, limit=REQUEST_LIMIT):
    payload = read_bytes(path, limit)
    require(expected_sha is None or sha256(payload) == digest(expected_sha))
    return json_bytes(payload)


def write_bytes(path, payload):
    with open_file(path, create=True) as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return sha256(payload)


def write_json(path, value, limit=RESULT_LIMIT):
    payload = canonical_bytes(value)
    require(len(payload) <= limit)
    return write_bytes(path, payload)


def execution_root(value):
    return "voxcpm-training/actions/" + identity(value["actionId"]) + "/executions/" + identity(value["executionId"])


def window_root(value):
    return execution_root(value) + "/windows/" + identity(value["windowId"])


def checkpoint_relative(value):
    return window_root(value) + "/checkpoints/" + identity(value.get("checkpointId", value.get("id"))) + "/manifest.json"


def validate_cursor(cursor, clip_ids):
    require(type(clip_ids) is list and 1 <= len(clip_ids) <= 512 and len(set(clip_ids)) == len(clip_ids))
    for item in clip_ids:
        identity(item)
    keys(cursor, ("globalStep", "completedEpochs", "epochSize", "nextRetainedOrdinal", "nextClipId"))
    size = integer(cursor["epochSize"], 1, 512)
    ordinal = integer(cursor["nextRetainedOrdinal"], 0, size - 1)
    require(size == len(clip_ids) and cursor["nextClipId"] == clip_ids[ordinal])
    require(integer(cursor["globalStep"], 0, 512) == integer(cursor["completedEpochs"], 0, 512) * size + ordinal)


def advance_cursor(cursor, clip_ids):
    validate_cursor(cursor, clip_ids)
    step = integer(cursor["globalStep"] + 1, 1, 512)
    epoch, ordinal = divmod(step, len(clip_ids))
    return dict(globalStep=step, completedEpochs=epoch, epochSize=len(clip_ids), nextRetainedOrdinal=ordinal, nextClipId=clip_ids[ordinal])


def validate_identity(value):
    keys(value, IDENTITY_KEYS)
    for key, item in value.items():
        digest(item, 40 if key in ("sourceCommit", "baseRevision") else 64)


def validate_layout(layout, expected_sha):
    require(type(layout) is list and 1 <= len(layout) <= 384)
    names, count = [], 0
    for entry in layout:
        keys(entry, ("name", "shape", "dtype"))
        name = entry["name"]
        require(type(name) is str and len(name) <= 256 and re.fullmatch(r"(?:base_lm|residual_lm|feat_decoder\.estimator)\.[A-Za-z0-9_.]+\.(?:q_proj|k_proj|v_proj|o_proj)\.lora_[AB]", name) is not None)
        names.append(name)
        shape = entry["shape"]
        require(entry["dtype"] == "F32" and type(shape) is list and len(shape) == 2)
        a, b = (integer(n, 1, 32768) for n in shape)
        require((a == 2 if name.endswith(".lora_A") else b == 2))
        count += a * b
    require(count <= 1130496 and names == sorted(set(names)) and sha256(canonical_bytes(layout)) == digest(expected_sha))


def validate_inventory(root, files, count):
    io_path(root)
    require(type(files) is dict and len(files) == count)
    folded = set()
    for name, entry in files.items():
        safe_path(root, name)
        require(name.casefold() not in folded)
        folded.add(name.casefold())
        keys(entry, ("bytes", "sha256"))
        integer(entry["bytes"], 0, 16 * 1024**3)
        digest(entry["sha256"])


def validate_parent(parent, request):
    if parent is None:
        require(request["cursor"]["globalStep"] == 0)
        return
    keys(parent, ("id", "trainingId", "actionId", "executionId", "windowId", "manifestRelativePath", "manifestSha256", "cursor"))
    for key in ("id", "trainingId", "actionId", "executionId", "windowId"):
        identity(parent[key])
    digest(parent["manifestSha256"])
    validate_cursor(parent["cursor"], request["clipIds"])
    require(parent["cursor"] == request["cursor"] and request["cursor"]["globalStep"] > 0)
    require(parent["trainingId"] == request["trainingId"] and parent["actionId"] == request["actionId"])
    require(parent["id"] != request["checkpointId"] and parent["manifestRelativePath"] == checkpoint_relative(parent))
    safe_path(request["projectRoot"], parent["manifestRelativePath"])


def validate_request(value):
    keys(value, (*ID_KEYS, *CORPUS_KEYS, "format", "projectRoot", "outputRelativeRoot", "identity", "clipIds", "cursor", "targetCompletedEpochs",
                 "selectedClip", "source", "parentCheckpoint", "dependencySites", "sourceRoot", "sourceFiles", "modelRoot", "modelFiles",
                 "runtimeInventoryPath", "runtimeInventorySha256", "cache", "parameterLayout"))
    require(type(value["format"]) is int and value["format"] == 1 and len(canonical_bytes(value)) <= REQUEST_LIMIT)
    for key in ID_KEYS + ("corpusVersionId",):
        identity(value[key])
    for key in ("corpusManifestSha256", "corpusBindingSha256", "runtimeInventorySha256"):
        digest(value[key])
    require(value["outputRelativeRoot"] == window_root(value))
    safe_path(value["projectRoot"], value["outputRelativeRoot"])
    validate_identity(value["identity"])
    validate_cursor(value["cursor"], value["clipIds"])
    require(integer(value["targetCompletedEpochs"], 1, 512) == value["cursor"]["completedEpochs"] + 1)
    require(value["cursor"]["globalStep"] < 512)
    clip = value["selectedClip"]
    keys(clip, ("trainingOrdinal", "sourceIndex", "id", "startSample", "endSample", "correctedText", "textSha256", "audioRelativePath", "audioSha256"))
    require(integer(clip["trainingOrdinal"], 0, 511) == value["cursor"]["nextRetainedOrdinal"] and identity(clip["id"]) == value["cursor"]["nextClipId"])
    integer(clip["sourceIndex"], 0, 511)
    start, end = integer(clip["startSample"], 0, 19200000), integer(clip["endSample"], 1, 19200000)
    require(1 <= end - start <= 256000)
    text = clip["correctedText"]
    require(type(text) is str and 1 <= len(text) <= 4000 and text == " ".join(text.split()))
    require(sha256(text.encode("utf-8")) == digest(clip["textSha256"]))
    digest(clip["audioSha256"])
    parts = relative_parts(clip["audioRelativePath"])
    require(len(parts) == 9 and parts[:2] == ["tts-corpus", "actions"] and parts[3] == "executions" and parts[5] == "windows" and parts[7] == "clips" and parts[8] == clip["id"] + ".wav")
    for index in (2, 4, 6):
        identity(parts[index])
    source = value["source"]
    keys(source, ("recordingId", "relativePath", "sha256", "sampleCount", "sampleRate"))
    identity(source["recordingId"])
    digest(source["sha256"])
    safe_path(value["projectRoot"], source["relativePath"])
    require(type(source["sampleRate"]) is int and source["sampleRate"] == 48000)
    require(end <= (integer(source["sampleCount"], 1, 57600000) + 2) // 3)
    validate_parent(value["parentCheckpoint"], value)
    sites = value["dependencySites"]
    require(type(sites) is list and 1 <= len(sites) <= 2 and len(set(sites)) == len(sites))
    for site in sites:
        io_path(site)
    io_path(value["runtimeInventoryPath"])
    validate_inventory(value["sourceRoot"], value["sourceFiles"], 38)
    validate_inventory(value["modelRoot"], value["modelFiles"], 7)
    cache = value["cache"]
    keys(cache, ("path", "bytes", "sha256", "layoutSha256"))
    io_path(cache["path"])
    require(type(cache["bytes"]) is int and cache["bytes"] == 9536873992)
    require(cache["sha256"] == value["identity"]["cacheSha256"] and cache["layoutSha256"] == value["identity"]["cacheLayoutSha256"])
    validate_layout(value["parameterLayout"], value["identity"]["adapterLayoutSha256"])
    return value


def manifest_bindings(request, request_sha):
    value = {key: request[key] for key in (*ID_KEYS, *CORPUS_KEYS, "identity", "parameterLayout")}
    value.update(format=1, requestSha256=digest(request_sha), parentManifestSha256=request["parentCheckpoint"]["manifestSha256"] if request["parentCheckpoint"] else None,
                 beforeCursor=request["cursor"], afterCursor=advance_cursor(request["cursor"], request["clipIds"]))
    return value


def validate_manifest(manifest, request, request_sha):
    wanted = manifest_bindings(request, request_sha)
    keys(manifest, (*wanted, "files", "metrics"))
    require(canonical_bytes({k: manifest[k] for k in wanted}) == canonical_bytes(wanted))
    keys(manifest["files"], PAYLOAD_LIMITS)
    for name, entry in manifest["files"].items():
        keys(entry, ("bytes", "sha256"))
        integer(entry["bytes"], 1, PAYLOAD_LIMITS[name])
        digest(entry["sha256"])
    keys(manifest["metrics"], ("loss", "gradientNorm"))
    finite(manifest["metrics"]["loss"])
    finite(manifest["metrics"]["gradientNorm"], 0)


def result_envelope(request, request_sha, checkpoint, stopped):
    require(type(stopped) is bool and (checkpoint is not None or stopped))
    value = {key: request[key] for key in (*ID_KEYS, *CORPUS_KEYS, "identity")}
    value.update(format=1, requestSha256=digest(request_sha), checkpoint=checkpoint, stopped=stopped)
    validate_result(request, request_sha, value)
    return value


def validate_result(request, request_sha, value):
    expected = {key: request[key] for key in (*ID_KEYS, *CORPUS_KEYS, "identity")}
    expected.update(format=1, requestSha256=digest(request_sha))
    keys(value, (*expected, "checkpoint", "stopped"))
    require(canonical_bytes({k: value[k] for k in expected}) == canonical_bytes(expected))
    require(type(value["stopped"]) is bool)
    reference = value["checkpoint"]
    if reference is None:
        require(value["stopped"])
        return
    keys(reference, ("id", "manifestRelativePath", "manifestSha256", "cursor"))
    require(reference["id"] == request["checkpointId"] and reference["manifestRelativePath"] == checkpoint_relative(request))
    digest(reference["manifestSha256"])
    require(canonical_bytes(reference["cursor"]) == canonical_bytes(advance_cursor(request["cursor"], request["clipIds"])))
