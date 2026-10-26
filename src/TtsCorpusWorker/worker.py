"""Entrypoint: python -I -S -u worker.py --request ... --request-sha256 ... --gate ..."""
import argparse
import contextlib
import os
from pathlib import Path
import sys
import time

# -I -S omits this directory too. Only the pinned own worker directory is added
# before START, never site-packages or any directory supplied through JSON.
if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
from protocol import (REQUEST_LIMIT, execution_root, io_path, no_reparse, read_json, require, result_envelope,
                      safe_path, sha256_file, validate_request, validate_result, verify_plan_files,
                      write_bytes, write_json)


def run_window(clips, stop_path, transcribe):
    accepted = []
    require(len(clips) <= 8)
    for clip in clips:
        no_reparse(stop_path)
        if stop_path.exists():
            return accepted, True
        accepted.append(transcribe(clip))
    no_reparse(stop_path)
    return accepted, stop_path.exists()


def dependencies(request):
    # Parent validates and pins runtime inventory independently. No .pth processing.
    for site in request["dependencySites"]:
        no_reparse(site)
        require(Path(site).is_dir())
        sys.path.append(site)
    for item in request["modelFiles"]:
        path = safe_path(request["modelRoot"], item["filename"])
        require(path.stat().st_size == item["bytes"] and sha256_file(path) == item["sha256"])
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1",
                      CUDA_VISIBLE_DEVICES="", OMP_NUM_THREADS="2", MKL_NUM_THREADS="2",
                      TOKENIZERS_PARALLELISM="false")


@contextlib.contextmanager
def quiet_libraries():
    """Suppress both Python streams and native writes to inherited stream fds."""
    sys.stdout.flush()
    sys.stderr.flush()
    with open(os.devnull, "w", encoding="utf-8") as sink:
        stdout_fd, stderr_fd = os.dup(1), os.dup(2)
        try:
            os.dup2(sink.fileno(), 1)
            os.dup2(sink.fileno(), 2)
            with contextlib.redirect_stdout(sink), contextlib.redirect_stderr(sink):
                yield
        finally:
            sink.flush()
            os.dup2(stdout_fd, 1)
            os.dup2(stderr_fd, 2)
            os.close(stdout_fd)
            os.close(stderr_fd)


def execute(request, request_sha, gate_path):
    gate_path = io_path(gate_path)
    output = safe_path(request["projectRoot"], request["outputRelativeRoot"])
    require(output.is_dir() and gate_path == output / "START")
    no_reparse(gate_path)
    # Open before announcing gated; the parent owns this pre-created file.
    with open(gate_path, "rb") as gate:
        print("tts_corpus.worker.gated", flush=True)
        deadline = time.monotonic() + 300
        while True:
            gate.seek(0)
            value = gate.read(7)
            if value == b"START\n":
                break
            require(value == b"" and time.monotonic() < deadline)
            time.sleep(0.05)
    stop = safe_path(request["projectRoot"], execution_root(request) + "/STOP")
    no_reparse(stop)
    plan, clips, stopped = None, [], stop.exists()
    if not stopped:
        # Optional library output can contain transcript or paths. Discard it;
        # only our fixed progress codes are emitted to stdout.
        print("tts_corpus.worker.preparing" if request["preparedPlan"] is None else "tts_corpus.worker.transcribing", flush=True)
        with quiet_libraries():
            dependencies(request)
            if request["preparedPlan"] is None:
                from prepare import prepare
                plan = prepare(request, output)
                stopped = stop.exists()
            else:
                verify_plan_files(request, request["preparedPlan"])
                require(sha256_file(safe_path(request["projectRoot"], request["sourceRelativePath"]),
                                    request["sourceSampleCount"] * 2 + 1024 * 1024) == request["sourceSha256"])
                from asr import load_pipeline, transcribe
                # A stop arriving during preflight must avoid unnecessary load.
                stopped = stop.exists()
                if not stopped:
                    engine = load_pipeline(request["modelRoot"])
                    texts = safe_path(request["projectRoot"], request["outputRelativeRoot"] + "/texts")
                    texts.mkdir(exist_ok=False)
                    start = request["nextClipIndex"]
                    requested = request["preparedPlan"]["clips"][start:start + 8]
                    clips, stopped = run_window(requested, stop, lambda c: transcribe(request, c, engine))
    result = result_envelope(request, request_sha, plan, clips, stopped)
    validate_result(request, request_sha, result)
    result_sha = write_json(output / "result.json", result)
    # READY is the final publication, never produced after a failure.
    write_bytes(output / "READY", (result_sha + "\n").encode("ascii"))
    print("tts_corpus.worker.ready", flush=True)


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--request", required=True)
    parser.add_argument("--request-sha256", required=True)
    parser.add_argument("--gate", required=True)
    args = parser.parse_args()
    try:
        request_path = io_path(args.request)
        require(request_path.is_absolute())
        request = validate_request(read_json(request_path, args.request_sha256, REQUEST_LIMIT))
        output = safe_path(request["projectRoot"], request["outputRelativeRoot"])
        require(request_path == output / "request.json")
        execute(request, args.request_sha256, Path(args.gate))
        return 0
    except Exception:
        # No paths, exception strings, or transcripts cross the stream boundary.
        print("tts_corpus.worker.failed", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
