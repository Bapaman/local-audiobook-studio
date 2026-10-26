"""Version 1 offline worker protocol. This module imports only the stdlib."""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import uuid

VERSION = 1
JSON_LIMIT = 8 * 1024 * 1024
REQUEST_LIMIT = 1024 * 1024
MAX_CLIPS = 512
MAX_SAMPLES = MAX_CLIPS * 16 * 16000
SHA = re.compile(r"[0-9a-f]{64}\Z")
ID = re.compile(r"[0-9a-f]{32}\Z")
WARNINGS = {"tts_corpus.asr.final_word_end_missing", "tts_corpus.asr.sparse_transcript"}


class ProtocolError(ValueError):
    """Safe failure: callers must never publish arbitrary exception messages."""


def require(condition):
    if not condition:
        raise ProtocolError("tts_corpus.worker.invalid_protocol")


def integer(value, minimum=0, maximum=MAX_SAMPLES):
    require(type(value) is int and minimum <= value <= maximum)
    return value


def digest(value):
    require(isinstance(value, str) and SHA.fullmatch(value) is not None)
    return value


def identity(value):
    require(isinstance(value, str) and ID.fullmatch(value) is not None)
    return value


def clip_identity(action_id, index):
    """Canonical Guid N; stable for an immutable action and clip index."""
    return uuid.uuid5(uuid.UUID(hex=identity(action_id)), "clip:" + str(integer(index, 0, MAX_CLIPS - 1))).hex


def io_path(path):
    """Filesystem spelling only; IPC keeps canonical logical/relative paths.

    Windows extended-length IO works without changing LongPathsEnabled. Admit
    only absolute local drive paths, including their exact extended spelling;
    never resolve traversal, UNC/device namespaces or reparse links here.
    """
    path = Path(path)
    if os.name != "nt":
        return path
    drive = path.drive
    if drive.startswith("\\\\?\\"):
        drive = drive[4:]
    require(path.is_absolute() and re.fullmatch(r"[A-Za-z]:", drive) is not None)
    require(all(part not in (".", "..") for part in path.parts[1:]))
    return path if path.drive.startswith("\\\\?\\") else Path("\\\\?\\" + str(path))


def no_reparse(path):
    path = io_path(path)
    for item in (path, *path.parents):
        try:
            info = item.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode))
        require(not getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def safe_path(root, relative):
    root = Path(root)
    require(root.is_absolute())
    require(isinstance(relative, str) and 0 < len(relative) <= 1024)
    require("\\" not in relative and ":" not in relative and not relative.startswith("/"))
    parts = relative.split("/")
    reserved = {"CON", "PRN", "AUX", "NUL", *("COM" + str(n) for n in range(1, 10)),
                *("LPT" + str(n) for n in range(1, 10))}
    for part in parts:
        require(part not in ("", ".", "..") and part[-1] not in (".", " "))
        require(not any(ord(c) < 32 or c in '<>"|?*' for c in part))
        require(part.split(".")[0].upper() not in reserved)
    path = io_path(root.joinpath(*parts))
    no_reparse(path)
    return path


def sha256_file(path, max_bytes=None):
    path = io_path(path)
    no_reparse(path)
    if max_bytes is not None:
        require(Path(path).stat().st_size <= max_bytes)
    result = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def canonical_bytes(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
                      allow_nan=False).encode("utf-8")


def write_bytes(path, payload):
    path = io_path(path)
    no_reparse(path)
    with open(path, "xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return hashlib.sha256(payload).hexdigest()


def write_json(path, value):
    payload = canonical_bytes(value)
    require(len(payload) <= JSON_LIMIT)
    return write_bytes(path, payload)


def read_json(path, expected_sha=None, limit=JSON_LIMIT):
    path = io_path(path)
    no_reparse(path)
    with open(path, "rb") as stream:
        payload = stream.read(limit + 1)
    require(len(payload) <= limit)
    if expected_sha is not None:
        require(hashlib.sha256(payload).hexdigest() == digest(expected_sha))
    def pairs(items):
        obj = {}
        for key, value in items:
            require(key not in obj)
            obj[key] = value
        return obj
    def invalid_constant(value):
        raise ProtocolError("tts_corpus.worker.invalid_protocol")
    try:
        return json.loads(payload.decode("utf-8"), object_pairs_hook=pairs, parse_constant=invalid_constant)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ProtocolError("tts_corpus.worker.invalid_protocol") from error


def action_root(request):
    return "tts-corpus/actions/" + identity(request["actionId"]) + "/executions/"


def execution_root(request):
    return action_root(request) + identity(request["executionId"])


def validate_plan(request, plan):
    require(isinstance(plan, dict))
    require(set(plan) == {"planRelativePath", "sha256", "sourceSha256", "runtimeFingerprint", "totalSamples", "clips"})
    digest(plan["sha256"])
    require(plan["sourceSha256"] == request["sourceSha256"])
    require(plan["runtimeFingerprint"] == request["runtimeFingerprint"])
    total = integer(plan["totalSamples"], 1)
    require(total == (request["sourceSampleCount"] + 2) // 3)
    relative = plan["planRelativePath"]
    safe_path(request["projectRoot"], relative)
    prefix = action_root(request)
    require(relative.startswith(prefix) and relative.endswith("/plan.json"))
    relative_parts = relative[len(prefix):].split("/")
    require(len(relative_parts) == 4 and relative_parts[1] == "windows" and relative_parts[3] == "plan.json")
    identity(relative_parts[0])
    identity(relative_parts[2])
    base = prefix + "/".join(relative_parts[:3])
    clips = plan["clips"]
    require(isinstance(clips, list) and 1 <= len(clips) <= MAX_CLIPS)
    end = 0
    for i, clip in enumerate(clips):
        require(isinstance(clip, dict) and set(clip) == {
            "id", "index", "startSample", "endSample", "audioRelativePath", "audioSha256"})
        require(identity(clip["id"]) == clip_identity(request["actionId"], i)
                and type(clip["index"]) is int and clip["index"] == i)
        require(integer(clip["startSample"]) == end)
        end = integer(clip["endSample"], end + 1)
        require(end - clip["startSample"] <= 256000)
        require(clip["audioRelativePath"] == base + "/clips/" + clip["id"] + ".wav")
        safe_path(request["projectRoot"], clip["audioRelativePath"])
        digest(clip["audioSha256"])
    require(end == total)
    return plan


def validate_request(request):
    require(isinstance(request, dict) and set(request) == {
        "schemaVersion", "requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "projectRoot",
        "outputRelativeRoot", "sourceRelativePath", "sourceRecordingId", "sourceSha256", "sourceSampleCount",
        "runtimeFingerprint", "preparedPlan", "nextClipIndex", "requestedClipIds", "dependencySites", "modelRoot", "modelFiles"})
    require(type(request["schemaVersion"]) is int and request["schemaVersion"] == VERSION)
    for key in ("requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId"):
        identity(request[key])
    for key in ("sourceSha256", "runtimeFingerprint"):
        digest(request[key])
    integer(request["sourceSampleCount"], 1, MAX_SAMPLES * 3)
    integer(request["nextClipIndex"], 0, MAX_CLIPS)
    require(request["outputRelativeRoot"] == execution_root(request) + "/windows/" + request["windowId"])
    safe_path(request["projectRoot"], request["outputRelativeRoot"])
    safe_path(request["projectRoot"], request["sourceRelativePath"])
    require(not request["sourceRelativePath"].startswith("tts-corpus/"))
    ids = request["requestedClipIds"]
    require(isinstance(ids, list) and len(ids) <= 8 and len(set(ids)) == len(ids))
    for item in ids:
        identity(item)
    plan = request["preparedPlan"]
    if plan is None:
        require(request["nextClipIndex"] == 0 and ids == [])
    else:
        validate_plan(request, plan)
        start = request["nextClipIndex"]
        require(start < len(plan["clips"]))
        require(ids == [c["id"] for c in plan["clips"][start:start + 8]])
    sites = request["dependencySites"]
    require(isinstance(sites, list) and 1 <= len(sites) <= 2)
    for site in sites:
        require(isinstance(site, str) and Path(site).is_absolute())
        no_reparse(site)
    require(isinstance(request["modelRoot"], str) and Path(request["modelRoot"]).is_absolute())
    no_reparse(request["modelRoot"])
    files = request["modelFiles"]
    require(isinstance(files, list) and 1 <= len(files) <= 32)
    names = set()
    for entry in files:
        require(isinstance(entry, dict) and set(entry) == {"filename", "bytes", "sha256"})
        safe_path(request["modelRoot"], entry["filename"])
        require("/" not in entry["filename"] and entry["filename"] not in names)
        names.add(entry["filename"])
        integer(entry["bytes"], 1, 4 * 1024 * 1024 * 1024)
        digest(entry["sha256"])
    return request


def verify_plan_files(request, plan):
    validate_plan(request, plan)
    disk = read_json(safe_path(request["projectRoot"], plan["planRelativePath"]), plan["sha256"], REQUEST_LIMIT)
    require(isinstance(disk, dict) and set(disk) == {
        "schemaVersion", "actionId", "sourceSha256", "runtimeFingerprint", "totalSamples", "clips",
        "cleanAudioRelativePath", "cleanAudioSha256"})
    require(type(disk["schemaVersion"]) is int and disk["schemaVersion"] == VERSION)
    # A matching hash and Python's coercive equality do not establish schema
    # types: false == 0 and 16000.0 == 16000. Validate the disk copy itself.
    disk_plan = {key: disk[key] for key in ("sourceSha256", "runtimeFingerprint", "totalSamples", "clips")}
    disk_plan.update(planRelativePath=plan["planRelativePath"], sha256=plan["sha256"])
    validate_plan(request, disk_plan)
    for key in ("sourceSha256", "runtimeFingerprint", "totalSamples", "clips"):
        require(canonical_bytes(disk[key]) == canonical_bytes(plan[key]))
    require(disk["actionId"] == request["actionId"])
    base = plan["planRelativePath"].removesuffix("/plan.json")
    require(disk["cleanAudioRelativePath"] == base + "/clean.wav")
    clean_path = safe_path(request["projectRoot"], disk["cleanAudioRelativePath"])
    require(sha256_file(clean_path, plan["totalSamples"] * 2 + 1024 * 1024) == digest(disk["cleanAudioSha256"]))
    # Bounds and PCM shape are independently checked by the native accepting parent too.
    from prepare import read_wav
    read_wav(clean_path, 16000, plan["totalSamples"])
    for clip in plan["clips"]:
        path = safe_path(request["projectRoot"], clip["audioRelativePath"])
        samples = clip["endSample"] - clip["startSample"]
        require(sha256_file(path, samples * 2 + 1024 * 1024) == clip["audioSha256"])
        read_wav(path, 16000, samples)
    return disk


def validate_prefix(request, ids, stopped):
    require(type(stopped) is bool and isinstance(ids, list))
    requested = request["requestedClipIds"]
    require(len(ids) <= len(requested) and ids == requested[:len(ids)])
    require(stopped or len(ids) == len(requested) > 0)


def validate_transcript(payload, duration):
    require(isinstance(payload, dict) and set(payload) == {"schemaVersion", "clipId", "draftText", "warningCodes", "words"})
    require(type(payload["schemaVersion"]) is int and payload["schemaVersion"] == VERSION)
    identity(payload["clipId"])
    text = payload["draftText"]
    require(isinstance(text, str) and len(text) <= 4000 and text == " ".join(text.split()))
    warnings = payload["warningCodes"]
    require(isinstance(warnings, list) and len(warnings) == len(set(warnings)) and set(warnings) <= WARNINGS)
    words = payload["words"]
    require(isinstance(words, list) and len(words) <= 2048)
    last = 0.0
    for i, word in enumerate(words):
        require(isinstance(word, dict) and set(word) == {"text", "start", "end"})
        require(isinstance(word["text"], str) and 0 < len(word["text"]) <= 4000)
        start, end = word["start"], word["end"]
        require(type(start) in (int, float) and math.isfinite(start) and last <= start <= duration)
        last = start
        if end is None:
            require(i == len(words) - 1 and "tts_corpus.asr.final_word_end_missing" in warnings)
        else:
            require(type(end) in (int, float) and math.isfinite(end) and start <= end <= duration)


def result_envelope(request, request_sha, prepared_plan, clips, stopped):
    result = {key: request[key] for key in ("requestId", "projectId", "corpusId", "actionId", "executionId", "windowId",
                                           "sourceRecordingId", "sourceSha256", "runtimeFingerprint")}
    result.update(schemaVersion=VERSION, requestSha256=digest(request_sha), preparedPlan=prepared_plan,
                  planSha256=(prepared_plan or request["preparedPlan"] or {}).get("sha256"),
                  clips=clips, stopped=stopped)
    return result


def validate_result(request, request_sha, result):
    require(isinstance(result, dict) and set(result) == {
        "schemaVersion", "requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId",
        "sourceSha256", "runtimeFingerprint", "requestSha256", "preparedPlan", "planSha256", "clips", "stopped"})
    require(type(result["schemaVersion"]) is int and result["schemaVersion"] == VERSION and type(result["stopped"]) is bool)
    for key in ("requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId", "sourceSha256", "runtimeFingerprint"):
        require(result[key] == request[key])
    require(result["requestSha256"] == digest(request_sha))
    clips = result["clips"]
    require(isinstance(clips, list) and len(clips) <= 8)
    plan = request["preparedPlan"]
    if plan is None:
        require(clips == [])
        if result["preparedPlan"] is None:
            require(result["stopped"] and result["planSha256"] is None)
            return
        plan = result["preparedPlan"]
        require(plan["planRelativePath"] == request["outputRelativeRoot"] + "/plan.json")
        verify_plan_files(request, plan)
    else:
        require(result["preparedPlan"] is None)
        validate_plan(request, plan)
        validate_prefix(request, [c["plan"]["id"] for c in clips], result["stopped"])
        for offset, accepted in enumerate(clips):
            require(isinstance(accepted, dict) and set(accepted) == {
                "plan", "textRelativePath", "textSha256", "draftText", "warningCodes", "words"})
            index = request["nextClipIndex"] + offset
            clip = plan["clips"][index]
            nested_clips = plan["clips"].copy()
            nested_clips[index] = accepted["plan"]
            validate_plan(request, dict(plan, clips=nested_clips))
            require(canonical_bytes(accepted["plan"]) == canonical_bytes(clip))
            require(accepted["textRelativePath"] == request["outputRelativeRoot"] + "/texts/" + clip["id"] + ".json")
            payload = read_json(safe_path(request["projectRoot"], accepted["textRelativePath"]), digest(accepted["textSha256"]))
            duration = (clip["endSample"] - clip["startSample"]) / 16000
            validate_transcript(payload, duration)
            duplicated_payload = {key: accepted[key] for key in ("draftText", "warningCodes", "words")}
            duplicated_payload.update(schemaVersion=VERSION, clipId=clip["id"])
            validate_transcript(duplicated_payload, duration)
            require(payload["clipId"] == clip["id"])
            for key in ("draftText", "warningCodes", "words"):
                require(canonical_bytes(payload[key]) == canonical_bytes(accepted[key]))
    require(result["planSha256"] == plan["sha256"])
