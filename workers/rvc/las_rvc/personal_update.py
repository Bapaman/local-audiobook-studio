"""One admitted native CPU update; no discovery, fake epoch or auto activation."""
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
import hashlib
from importlib.metadata import version
import json
import math
from pathlib import Path
import random
import re
import shutil
import time

from .checkpoints import CheckpointError, RunIdentity
from .experimental_intake import evaluate_experimental_source
from .feature_corpus import CorpusRef, _read_previous, load_persisted_preview
from .feature_models import FEATURE_PROFILE
from .features import load_prepared_clips
from .owned_files import pin_chain, pinned_file, plain_path
from .training_admission import _raw, _read_preflight, make_training_split, TRAINING_PRESET


@dataclass(frozen=True)
class TrainingInputs:
    dataset_root: Path
    dataset_sha256: str
    preflight: CorpusRef
    corpus: CorpusRef
    candidate: CorpusRef
    admission: CorpusRef
    source_root: Path
    base_asset_root: Path


@dataclass(frozen=True)
class AdmittedTraining:
    decision: dict
    corpus: dict
    preview_refs: tuple
    train_ids: tuple

    def run_identity(self, run_id):
        return RunIdentity(run_id, self.decision["input"]["corpus_manifest_sha256"],
            self.decision["runtime_sha256"], self.decision["config_sha256"],
            self.decision["runtime_identity"]["rvc_commit"], TRAINING_PRESET["id"], 1, 40000, True)


def _position(epoch, ordinal, size):
    if (type(epoch) is not int or not 0 <= epoch < 200 or type(size) is not int or not 1 <= size <= 2048
            or type(ordinal) is not int or not 0 <= ordinal < size):
        raise CheckpointError("training.personal.cursor.invalid")


def choose_training_clip(train_ids, epoch, ordinal, seed):
    if (type(train_ids) not in (list, tuple) or not 1 <= len(train_ids) <= 2048
            or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in train_ids)
            or list(train_ids) != sorted(set(train_ids))
            or type(seed) is not int or not 0 <= seed <= 2 ** 32 - 1):
        raise CheckpointError("training.personal.cursor.invalid")
    _position(epoch, ordinal, len(train_ids))
    order = list(train_ids)
    random.Random(seed + epoch).shuffle(order)
    return order[ordinal]


def advance_update_cursor(epoch, ordinal, size):
    _position(epoch, ordinal, size)
    return (epoch + 1, 0, True) if ordinal + 1 == size else (epoch, ordinal + 1, False)


def plan_training_window(train_ids, epoch, ordinal, seed, max_updates):
    if type(max_updates) is not int or not 1 <= max_updates <= 2:
        raise CheckpointError("training.personal.window_cap.invalid")
    planned = []
    for _ in range(max_updates):
        if epoch == 200 and planned:
            break
        planned.append((choose_training_clip(train_ids, epoch, ordinal, seed), epoch, ordinal))
        epoch, ordinal, _ = advance_update_cursor(epoch, ordinal, len(train_ids))
    return tuple(planned)


def should_start_window_update(elapsed_seconds, completed_updates):
    if (type(elapsed_seconds) not in (int, float) or not math.isfinite(elapsed_seconds) or elapsed_seconds < 0
            or type(completed_updates) is not int or not 0 <= completed_updates <= 2):
        raise CheckpointError("training.personal.window_budget.invalid")
    return elapsed_seconds < 25


def make_update_control(point, *, start_global_step=None, completed_updates=1):
    """Private returned point identity, not a hash adopted by directory discovery."""
    from .update_checkpoints import UpdateCheckpoint
    if type(point) is not UpdateCheckpoint:
        raise CheckpointError("training.personal.control.invalid")
    if start_global_step is None:
        start_global_step = point.global_step - 1
    if (type(start_global_step) is not int or start_global_step < 0
            or type(completed_updates) is not int or not 1 <= completed_updates <= 2
            or start_global_step + completed_updates != point.global_step):
        raise CheckpointError("training.personal.control.invalid")
    return {"phase": "personal_checkpoint_control", "checkpoint_root": str(point.directory),
        "checkpoint_manifest_sha256": point.manifest_sha256, "run_id": point.identity.run_id,
        "admission_sha256": point.position.admission_sha256, "global_step": point.global_step,
        "completed_epoch": point.completed_epoch, "next_ordinal": point.position.next_ordinal,
        "epoch_size": point.position.epoch_size, "start_global_step": start_global_step,
        "completed_updates": completed_updates}


def publish_update_acceptance(control, output_root, *, worker_exit_code, worker_timed_out, deadline_ms,
        request_sha256, expected_epoch_size, expected_previous, expected_start_step=0, max_updates=1):
    """Cheap post-exit supervisor receipt. Never grants admission or loads Torch."""
    if (type(worker_exit_code) is not int or worker_exit_code != 0 or worker_timed_out is not False
            or type(deadline_ms) is not int or deadline_ms != 60000):
        raise CheckpointError("training.personal.supervisor.unaccepted")
    if (type(expected_start_step) is not int or expected_start_step < 0
            or type(max_updates) is not int or not 1 <= max_updates <= 2
            or type(expected_epoch_size) is not int or not 1 <= expected_epoch_size <= 2048
            or type(request_sha256) is not str or re.fullmatch(r"[0-9a-f]{64}", request_sha256) is None):
        raise CheckpointError("training.personal.control.invalid")
    try:
        if expected_start_step == 0:
            if expected_previous is not None: raise ValueError("initial predecessor")
        elif (type(expected_previous) is not dict or set(expected_previous) != {"root", "sha256"}
                or type(expected_previous["sha256"]) is not str
                or re.fullmatch(r"[0-9a-f]{64}", expected_previous["sha256"]) is None):
            raise ValueError("known predecessor")
        else:
            plain_path(Path(expected_previous["root"]))
        with ExitStack() as pins:
            root = plain_path(Path(output_root))
            pin_chain(pins, root)
            checkpoint = root / "checkpoint"
            pin_chain(pins, checkpoint)
            if ({p.name for p in root.iterdir()} != {"checkpoint"}
                    or {p.name for p in checkpoint.iterdir()} != {"generator.pt", "discriminator.pt", "training.pt", "manifest.json", "READY"}
                    or type(control) is not dict or control.get("checkpoint_root") != str(checkpoint)
                    or type(control.get("checkpoint_manifest_sha256")) is not str
                    or re.fullmatch(r"[0-9a-f]{64}", control["checkpoint_manifest_sha256"]) is None):
                raise ValueError("owned point control")
            stream = pins.enter_context(pinned_file(checkpoint / "manifest.json"))
            raw = stream.read(65537)
            ready = pins.enter_context(pinned_file(checkpoint / "READY"))
            if (len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != control["checkpoint_manifest_sha256"]
                    or ready.read(66) != control["checkpoint_manifest_sha256"].encode() + b"\n"):
                raise ValueError("returned point identity")
            manifest = json.loads(raw)
            if manifest["format"] != 2 or manifest["purpose"] != "complete-update-checkpoint":
                raise ValueError("v2 complete point")
            position = manifest["position"]
            from .update_checkpoints import UpdatePosition
            UpdatePosition(**position)  # Strict cursor types/bounds, including the final epoch.
            count = control.get("completed_updates")
            if (type(count) is not int or not 1 <= count <= max_updates
                    or type(control.get("start_global_step")) is not int
                    or control["start_global_step"] != expected_start_step
                    or expected_start_step + count != position["global_step"]
                    or position["epoch_size"] != expected_epoch_size
                    or (position["completed_epoch"], position["next_ordinal"]) != divmod(expected_start_step + count, expected_epoch_size)):
                raise ValueError("request-bound actual step delta")
            expected = {"phase": "personal_checkpoint_control", "checkpoint_root": str(checkpoint),
                "checkpoint_manifest_sha256": control["checkpoint_manifest_sha256"], "run_id": manifest["identity"]["run_id"],
                "admission_sha256": position["admission_sha256"], "global_step": position["global_step"],
                "completed_epoch": position["completed_epoch"], "next_ordinal": position["next_ordinal"],
                "epoch_size": position["epoch_size"], "start_global_step": expected_start_step,
                "completed_updates": count}
            if _raw(control) != _raw(expected):
                raise ValueError("point cursor identity")
            receipt = {"format": 2, "purpose": "supervisor-accepted-personal-native-update", "point": control,
                "request_sha256": request_sha256, "previous_checkpoint": expected_previous,
                "native_worker_exit_code": 0, "native_worker_timed_out": False,
                "native_worker_deadline_ms": 60000, "accepted_after_native_worker_exit": True,
                "quality_established": False, "auto_activate_voice": False}
            from .feature_models import _write_bytes
            raw = _raw(receipt)
            _write_bytes(root / "accepted-update.json", raw)
            stream = pins.enter_context(pinned_file(root / "accepted-update.json"))
            if stream.read(len(raw) + 1) != raw:
                raise ValueError("receipt publication")
            return receipt
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.personal.supervisor.invalid") from None


def read_expected_window(request_path, request_sha256, output_root):
    """Cheap known request/predecessor replay for supervisor, no Torch imports."""
    try:
        with ExitStack() as pins:
            stream = pins.enter_context(pinned_file(plain_path(Path(request_path))))
            raw = stream.read(65537)
            if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != request_sha256:
                raise ValueError("known request")
            request = json.loads(raw)
            maximum = request.get("max_updates", 1)
            if (type(request["format"]) is not int or request["format"] != 1
                    or type(maximum) is not int or not 1 <= maximum <= 2
                    or set(request) - {"format", "purpose", "run_id", "inputs", "output_root", "previous", "max_updates"}
                    or request["purpose"] != ("one-experimental-personal-native-update" if maximum == 1
                        else "bounded-experimental-personal-native-window")
                    or str(plain_path(Path(request["output_root"]))) != str(plain_path(Path(output_root)))):
                raise ValueError("bounded request")
            start = 0
            admission_ref = request["inputs"]["admission"]
            if type(admission_ref) is not dict or set(admission_ref) != {"root", "sha256"}:
                raise ValueError("known admission")
            admission = _read_decision(pins, CorpusRef(Path(admission_ref["root"]), admission_ref["sha256"]))
            ids = admission["split"]["train_clip_ids"]
            if (type(admission["format"]) is not int or admission["format"] != 1
                    or admission["purpose"] != "experimental-native-rvc-training-admission" or admission["training_admitted"] is not True
                    or type(ids) is not list or not 1 <= len(ids) <= 2048
                    or any(type(i) is not str or re.fullmatch(r"clip-[0-9]{6}", i) is None for i in ids)
                    or ids != sorted(set(ids))):
                raise ValueError("admission epoch membership")
            epoch_size = len(ids)
            if request["previous"] is not None:
                previous = request["previous"]
                if type(previous) is not dict or set(previous) != {"root", "sha256"}:
                    raise ValueError("known predecessor reference")
                root = plain_path(Path(previous["root"]))
                stream = pins.enter_context(pinned_file(root / "manifest.json"))
                raw = stream.read(65537)
                ready = pins.enter_context(pinned_file(root / "READY"))
                if (len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != previous["sha256"]
                        or ready.read(66) != previous["sha256"].encode() + b"\n"):
                    raise ValueError("known predecessor")
                point = json.loads(raw)
                from .update_checkpoints import UpdatePosition
                position = UpdatePosition(**point["position"])
                if (type(point["format"]) is not int or point["format"] != 2 or point["purpose"] != "complete-update-checkpoint"
                        or point["identity"]["run_id"] != request["run_id"]
                        or position.admission_sha256 != admission_ref["sha256"] or position.epoch_size != epoch_size):
                    raise ValueError("same run/admission predecessor")
                start = point["position"]["global_step"]
            if type(start) is not int or start < 0:
                raise ValueError("starting step")
            return request, start, maximum, epoch_size
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.personal.request.invalid") from None


def _read_decision(pins, ref):
    if (type(ref) is not CorpusRef or type(ref.sha256) is not str
            or re.fullmatch(r"[0-9a-f]{64}", ref.sha256) is None):
        raise ValueError("explicit known decision")
    root = plain_path(Path(ref.root))
    pin_chain(pins, root)
    if {p.name for p in root.iterdir()} != {"decision.json", "READY"}:
        raise ValueError("decision inventory")
    stream = pins.enter_context(pinned_file(root / "decision.json"))
    raw = stream.read(2 * 1024 ** 2 + 1)
    ready = pins.enter_context(pinned_file(root / "READY"))
    if (len(raw) > 2 * 1024 ** 2 or hashlib.sha256(raw).hexdigest() != ref.sha256
            or ready.read(66) != ref.sha256.encode() + b"\n"):
        raise ValueError("decision identity")
    return json.loads(raw)


@contextmanager
def verify_admitted_training(inputs):
    """Keep source, known decisions, full-corpus and runtime asset pins alive."""
    try:
        with ExitStack() as pins:
            if type(inputs) is not TrainingInputs:
                raise ValueError("input contract")
            candidate = _read_decision(pins, inputs.candidate)
            decision = _read_decision(pins, inputs.admission)
            if (type(candidate["format"]) is not int or candidate["format"] != 1
                    or candidate["purpose"] != "experimental-native-rvc-training-candidate"
                    or candidate["training_admitted"] is not False or candidate["experimental_opt_in"] is not True
                    or candidate["auto_activate_voice"] is not False or candidate["quality_established"] is not False
                    or candidate["speech_duration_verified"] is not False):
                raise ValueError("candidate contract")
            expected = {**candidate, "purpose": "experimental-native-rvc-training-admission", "training_admitted": True,
                "supervisor_acceptance": {"candidate_sha256": inputs.candidate.sha256,
                    "native_worker_exit_code": 0, "native_worker_timed_out": False,
                    "native_worker_deadline_ms": 60000, "accepted_after_native_worker_exit": True}}
            if _raw(decision) != _raw(expected):
                raise ValueError("post-exit supervisor acceptance")
            prepared = pins.enter_context(load_prepared_clips(inputs.dataset_root, inputs.dataset_sha256, ["clip-000000"]))
            stream = pins.enter_context(pinned_file(Path(inputs.dataset_root) / "manifest.json"))
            raw = stream.read(2 * 1024 ** 2 + 1)
            if len(raw) > 2 * 1024 ** 2 or hashlib.sha256(raw).hexdigest() != inputs.dataset_sha256:
                raise ValueError("prepared identity")
            manifest = json.loads(raw)
            ordered = [c["id"] for c in manifest["clips"]]
            identity = {"manifest_sha256": prepared.manifest_sha256, "dataset_id": prepared.dataset_id,
                "dataset_clip_count": prepared.dataset_clip_count, "training_eligible": prepared.training_eligible}
            preflight = _read_preflight(pins, inputs.preflight,
                evaluate_experimental_source(manifest, opt_in=True), identity, ordered)
            split = make_training_split(manifest, opt_in=True)
            if (decision["input"] != {"prepared_manifest_sha256": inputs.dataset_sha256,
                    "dataset_id": prepared.dataset_id, "source_preflight_sha256": inputs.preflight.sha256,
                    "corpus_manifest_sha256": inputs.corpus.sha256}
                    or decision["original_preparation"] != preflight["original_preparation"]
                    or decision["warnings"] != preflight["warnings"] or decision["source_metrics"] != preflight["metrics"]
                    or decision["split"] != split or decision["preset"] != TRAINING_PRESET
                    or decision["preset_sha256"] != hashlib.sha256(_raw(TRAINING_PRESET)).hexdigest()):
                raise ValueError("source/split/preset replay")
            corpus, refs = _read_previous(pins, inputs.corpus, identity)
            shard_ids = [i for s in corpus["shards"] for i in s["selected_clip_ids"]]
            if (any(type(s["selected_clip_ids"]) is not list or not 1 <= len(s["selected_clip_ids"]) <= 3
                    for s in corpus["shards"]) or len(set(shard_ids)) != len(shard_ids) or set(shard_ids) != set(ordered)):
                raise ValueError("full unique corpus membership")
            clips = {c["id"]: c for c in manifest["clips"]}
            native = []
            for clip_id in shard_ids:
                clip = clips[clip_id]
                h, f = (clip["samples16k"] - 400) // 320 + 1, clip["samples16k"] // 160 + 1
                length = min(2 * h, f, clip["samples40k"] // 400)
                native.append({"id": clip_id, "frames": length, "phone_shape": [1, length, 768],
                    "spec_shape": [1, 1025, length], "wave_shape": [1, 1, length * 400]})
            expected_corpus = {"format": 1, "purpose": "verified-feature-corpus", "profile": FEATURE_PROFILE,
                "input": identity, "shards": corpus["shards"], "accepted_clip_ids": ordered,
                "pending_clip_ids": [], "full_dataset_extracted": True, "training_admitted": False,
                "aligned_frame_total": sum(c["frames"] for c in native),
                "frame_total_method": "sum-of-aligned-frames-not-unique-source-or-speech-duration",
                "exclusions": [], "speech_duration_seconds": None, "speech_duration_verified": False}
            if (corpus != expected_corpus or decision["native_validation"] != {
                    "method": "real-native-cpu-batch-for-every-accepted-clip", "verified_clips": len(native), "clips": native}):
                raise ValueError("native/full corpus geometry")
            # Native import only after all explicit known decision/source gates.
            from .rvc_kernel import SOURCE_MANIFEST, BASE_GENERATOR, BASE_DISCRIMINATOR, RVC_COMMIT, _verify_stream
            runtime = {"torch": version("torch"), "numpy": version("numpy"), "transformers": version("transformers"),
                "device": "cpu", "dtype": "float32", "threads": 2}
            if (runtime["torch"] != "2.10.0+cpu" or runtime["numpy"] != "1.26.4" or runtime["transformers"] != "4.49.0"):
                raise CheckpointError("training.runtime.incompatible")
            base_pins = {"f0G40k.pth": BASE_GENERATOR, "f0D40k.pth": BASE_DISCRIMINATOR}
            runtime_identity = {"runtime": runtime, "kernel_source_pins": SOURCE_MANIFEST,
                "base_model_pins": base_pins, "rvc_commit": RVC_COMMIT}
            if (_raw(decision["runtime_identity"]) != _raw(runtime_identity)
                    or decision["runtime_sha256"] != hashlib.sha256(_raw(runtime_identity)).hexdigest()
                    or decision["config_sha256"] != SOURCE_MANIFEST["configs/v1/40k.json"][1]):
                raise ValueError("runtime/config/source identity")
            for root, files in ((Path(inputs.source_root), SOURCE_MANIFEST), (Path(inputs.base_asset_root), base_pins)):
                root = plain_path(root)
                pin_chain(pins, root)
                for relative, expected in files.items():
                    _verify_stream(pins.enter_context(pinned_file(root / relative)), expected)
            yield AdmittedTraining(decision, corpus, tuple(refs), tuple(split["train_clip_ids"]))
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.personal.admission.invalid") from None


@dataclass(frozen=True)
class WindowResult:
    point: object
    start_global_step: int
    completed_updates: int


def run_personal_update(inputs, run_id, output_root, *, previous=None, emit=lambda event: None):
    return run_personal_window(inputs, run_id, output_root, previous=previous, max_updates=1, emit=emit).point


def run_personal_window(inputs, run_id, output_root, *, previous=None, max_updates=2, emit=lambda event: None):
    """Explicit <=2 actual D+G updates, one final v2 save, caller fixed60s."""
    if type(max_updates) is not int or not 1 <= max_updates <= 2:
        raise CheckpointError("training.personal.window_cap.invalid")
    started = time.monotonic()
    with ExitStack() as pins:
        root = plain_path(Path(output_root))
        pin_chain(pins, root)
        if any(root.iterdir()):
            raise CheckpointError("training.personal.output_not_empty")
        if shutil.disk_usage(root).free < 8 * 1024 ** 3:
            raise CheckpointError("training.personal.disk_space.insufficient")
        admitted = pins.enter_context(verify_admitted_training(inputs))
        identity = admitted.run_identity(run_id)
        from .update_checkpoints import load_update_checkpoint, make_update_position, save_update_checkpoint
        from .rvc_kernel import create_rvc_state, train_rvc_batch
        from .feature_batch import build_native_feature_batch
        seed = TRAINING_PRESET["sampler_seed"]
        checkpoint = None
        epoch, ordinal = 0, 0
        if previous is not None:
            if type(previous) is not CorpusRef:
                raise CheckpointError("training.update.checkpoint.invalid")
            checkpoint = load_update_checkpoint(previous.root, previous.sha256, identity,
                admitted.train_ids, inputs.admission.sha256, seed)
            epoch, ordinal = checkpoint.position.completed_epoch, checkpoint.position.next_ordinal
        start_step = checkpoint.global_step if checkpoint is not None else 0
        batches, loaded = [], {}
        for clip_id, _, _ in plan_training_window(admitted.train_ids, epoch, ordinal, seed, max_updates):
            shard = next(s for s in admitted.corpus["shards"] if clip_id in s["selected_clip_ids"])
            key = shard["manifest_sha256"]
            if key not in loaded:
                ref = next(r for r in admitted.preview_refs if str(r.root) == shard["root"] and r.sha256 == key)
                selected = pins.enter_context(load_prepared_clips(inputs.dataset_root, inputs.dataset_sha256, shard["selected_clip_ids"]))
                stored = pins.enter_context(load_persisted_preview(ref, selected))
                loaded[key] = selected, stored
            selected, stored = loaded[key]
            prepared = next(c for c in selected.clips if c.id == clip_id)
            clip = next(c for c in stored.clips if c.id == clip_id)
            batch = build_native_feature_batch(inputs.source_root, prepared.audio40, clip.phone, clip.f0)
            batches.append(batch)
            emit({"phase": "personal_batch_verified", "frames": int(batch.phone_lengths[0]), "resuming": checkpoint is not None})
        state = create_rvc_state(inputs.source_root, Path(inputs.base_asset_root) / "f0G40k.pth",
            Path(inputs.base_asset_root) / "f0D40k.pth")
        if checkpoint is not None:
            state.restore(checkpoint)  # Restore RNG last, no random batch/factory work follows.
        emit({"phase": "personal_native_state_ready", "resuming": checkpoint is not None})
        import torch
        completed = 0
        for batch in batches:
            if max_updates > 1 and not should_start_window_update(time.monotonic() - started, completed):
                if completed == 0:
                    raise CheckpointError("training.personal.window_budget.insufficient")
                emit({"phase": "personal_window_save_reserve", "completed_updates": completed})
                break
            g_probe = state.generator.enc_p.emb_phone.weight
            d_probe = next(state.discriminator.parameters())
            g_before, d_before = g_probe.detach().clone(), d_probe.detach().clone()
            losses = train_rvc_batch(state, batch)
            if torch.equal(g_before, g_probe.detach()) or torch.equal(d_before, d_probe.detach()):
                state._training_failed = True
                raise CheckpointError("training.personal.weights_unchanged")
            epoch, ordinal, rollover = advance_update_cursor(epoch, ordinal, len(admitted.train_ids))
            if rollover:
                state.scheduler_g.step()
                state.scheduler_d.step()
            completed += 1
            position = make_update_position(epoch, ordinal, admitted.train_ids, seed, inputs.admission.sha256)
            emit({"phase": "personal_native_update_complete", "global_step": position.global_step,
                "completed_epoch": epoch, "next_ordinal": ordinal, "both_weights_changed": True, **losses})
        point = save_update_checkpoint(state, identity, position, admitted.train_ids, root)
        emit({"phase": "personal_checkpoint_saved", "global_step": position.global_step,
            "completed_epoch": epoch, "next_ordinal": ordinal, "quality_established": False,
            "auto_activate_voice": False, "completed_updates": completed})
        return WindowResult(point, start_step, completed)
