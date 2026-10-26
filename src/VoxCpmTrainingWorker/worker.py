"""python -I -S -B -u worker.py --request ... --request-sha256 ... --gate ..."""
import argparse
import contextlib
import os
from pathlib import Path
import sys
import time

if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).absolute().parent))
from owned_files import io_path, open_file, pin_directories, exists_plain_file
from protocol import (REQUEST_LIMIT, PAYLOAD_LIMITS, MANIFEST_LIMIT, ProtocolError, require, keys, read_json,
                      read_bytes, write_bytes, write_json, validate_request, safe_path, sha256,
                      execution_root, checkpoint_relative, manifest_bindings, result_envelope)
from checkpoint import preflight_bundle


@contextlib.contextmanager
def quiet_libraries():
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


def _publish_candidate(request, request_sha, candidate, output):
    # train_one returns bounded payload bytes after the update, not paths picked
    # by a library. This wrapper exclusively owns all final publication names.
    keys(candidate, ("payloads", "metrics"))
    keys(candidate["payloads"], PAYLOAD_LIMITS)
    for name, limit in PAYLOAD_LIMITS.items():
        require(type(candidate["payloads"][name]) is bytes and 0 < len(candidate["payloads"][name]) <= limit)
    parent = output / "checkpoints"
    parent.mkdir(exist_ok=False)
    with pin_directories(parent):
        root = parent / request["checkpointId"]
        root.mkdir(exist_ok=False)
        with pin_directories(root):
            files = {}
            for name, payload in candidate["payloads"].items():
                files[name] = dict(bytes=len(payload), sha256=write_bytes(root / name, payload))
            manifest = manifest_bindings(request, request_sha)
            manifest.update(files=files, metrics=candidate["metrics"])
            # Schema validation precedes READY; preflight then validates payload
            # grammar before the enclosing window can ever become ready.
            from protocol import validate_manifest
            validate_manifest(manifest, request, request_sha)
            manifest_sha = write_json(root / "manifest.json", manifest, MANIFEST_LIMIT)
            write_bytes(root / "READY", (manifest_sha + "\n").encode("ascii"))
            preflight_bundle(root, manifest, dict(request=request, requestSha256=request_sha))
            return dict(id=request["checkpointId"], manifestRelativePath=checkpoint_relative(request), manifestSha256=manifest_sha, cursor=manifest["afterCursor"])


def execute(request, request_sha, gate_path):
    validate_request(request)
    output = safe_path(request["projectRoot"], request["outputRelativeRoot"])
    require(io_path(gate_path) == output / "START")
    with pin_directories(output), open_file(gate_path, mutable=True) as gate:
        print("voxcpm_training.worker.gated", flush=True)
        deadline = time.monotonic() + 300
        while True:
            gate.seek(0)
            value = gate.read(7)
            if value == b"START\n":
                break
            require(value == b"" and time.monotonic() < deadline)
            time.sleep(0.05)
        stop = safe_path(request["projectRoot"], execution_root(request) + "/STOP")
        stopped, reference = exists_plain_file(stop), None
        if not stopped:
            # Capture the permitted output stream before silencing native and
            # Python library writes. phase accepts only our fixed enum below.
            print("voxcpm_training.worker.loading", flush=True)
            progress_fd = os.dup(1)
            try:
                def phase(name):
                    require(name in ("loading", "training", "checkpointing"))
                    os.write(progress_fd, ("voxcpm_training.worker." + name + "\n").encode("ascii"))
                with quiet_libraries():
                    from trainer import train_one
                    candidate = train_one(request, phase)
                if candidate is None:
                    require(exists_plain_file(stop))
                    stopped = True
                else:
                    phase("checkpointing")
                    reference = _publish_candidate(request, request_sha, candidate, output)
                    stopped = exists_plain_file(stop)
            finally:
                os.close(progress_fd)
        result = result_envelope(request, request_sha, reference, stopped)
        result_sha = write_json(output / "result.json", result)
        write_bytes(output / "READY", (result_sha + "\n").encode("ascii"))
        print("voxcpm_training.worker.ready", flush=True)


def main():
    try:
        class SafeParser(argparse.ArgumentParser):
            def error(self, message):
                raise ProtocolError()
        parser = SafeParser(add_help=False, exit_on_error=False)
        parser.add_argument("--request", required=True)
        parser.add_argument("--request-sha256", required=True)
        parser.add_argument("--gate", required=True)
        args = parser.parse_args()
        request = validate_request(read_json(args.request, args.request_sha256, REQUEST_LIMIT))
        output = safe_path(request["projectRoot"], request["outputRelativeRoot"])
        require(io_path(args.request) == output / "request.json")
        execute(request, args.request_sha256, Path(args.gate))
        return 0
    except (Exception, SystemExit):
        print("voxcpm_training.worker.failed", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
