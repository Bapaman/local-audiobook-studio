"""Persistent admitted CPU2 training. The old fixed-60s probes are unchanged."""
from contextlib import ExitStack
from dataclasses import asdict
import hashlib
from pathlib import Path
import shutil
import time

from .checkpoints import CheckpointError, _json, _write_new
from .owned_files import pin_chain, plain_path, pinned_file
from .personal_session import (CHECKPOINT_INTERVAL, FREE_GUARD_BYTES, MAX_POINTS, POINT_BYTES,
    expected_position, session_stop_reason)


def validate_session_bootstrap(root):
    """An owned early STOP is valid; no other pre-existing output is adopted."""
    try:
        with ExitStack() as pins:
            root = plain_path(Path(root))
            pin_chain(pins, root)
            names = {entry.name for entry in root.iterdir()}
            if names - {"STOP"}:
                raise CheckpointError("training.session.output_or_space.invalid")
            if not names:
                return False
            marker = pins.enter_context(pinned_file(root / "STOP"))
            if marker.seek(0, 2) > 64:
                raise CheckpointError("training.session.stop.invalid")
            return True
    except CheckpointError:
        raise
    except Exception:
        raise CheckpointError("training.session.stop.invalid") from None


def run_session_updates(state, batches, ids, identity, admission_sha, previous, root, request_sha,
        start_step, target_step, *, train_step, emit=lambda event: None, stop_requested=lambda: False,
        elapsed=lambda: 0):
    """Complete real optimizer updates; injectable tiny CPU fixture is not RVC proof."""
    from .personal_update import advance_update_cursor, choose_training_clip
    from .update_checkpoints import make_update_position, save_update_checkpoint
    import torch
    if set(batches) != set(ids) or expected_position(start_step, ids, admission_sha)["sampler_seed"] != state.sampler_seed:
        raise CheckpointError("training.session.batches.invalid")
    root = plain_path(Path(root))
    points = root / "points"
    points.mkdir()
    epoch, ordinal = divmod(start_step, len(ids))
    step, last_saved, predecessor, entries = start_step, start_step, previous, []
    reason = session_stop_reason(step, target_step, elapsed(), stop_requested(), 0)
    while reason is None:
        clip_id = choose_training_clip(ids, epoch, ordinal, state.sampler_seed)
        emit({"phase": "session_update_started", "global_step": step + 1, "clip_id": clip_id})
        g_probe, d_probe = next(state.generator.parameters()), next(state.discriminator.parameters())
        g_before, d_before = g_probe.detach().clone(), d_probe.detach().clone()
        losses = train_step(state, batches[clip_id])
        if torch.equal(g_before, g_probe.detach()) or torch.equal(d_before, d_probe.detach()):
            state._training_failed = True
            raise CheckpointError("training.session.weights_unchanged")
        epoch, ordinal, rollover = advance_update_cursor(epoch, ordinal, len(ids))
        if rollover:
            state.scheduler_g.step()
            state.scheduler_d.step()
        step += 1
        emit({"phase": "session_update_complete", "global_step": step, "completed_epoch": epoch,
            "next_ordinal": ordinal, **losses})
        reason = session_stop_reason(step, target_step, elapsed(), stop_requested(), len(entries))
        if step - last_saved == CHECKPOINT_INTERVAL or reason is not None:
            if len(entries) >= MAX_POINTS or shutil.disk_usage(root).free < FREE_GUARD_BYTES:
                raise CheckpointError("training.session.disk_budget.insufficient")
            index = len(entries) + 1
            point_root = points / f"point-{index:03d}"
            point_root.mkdir()
            emit({"phase": "session_save_started", "global_step": step, "point_count": index})
            point = save_update_checkpoint(state, identity,
                make_update_position(epoch, ordinal, ids, state.sampler_seed, admission_sha), ids, point_root,
                max_point_bytes=POINT_BYTES)
            ref = {"root": str(point.directory), "sha256": point.manifest_sha256}
            entries.append({"point": ref, "previous": predecessor, "global_step": step})
            predecessor, last_saved = ref, step
            snapshot = {"format": 1, "purpose": "personal-session-candidate-journal", "request_sha256": request_sha,
                "start_global_step": start_step, "entries": entries, "stop_reason": reason}
            _write_new(root / f"journal-{index:03d}.json", _json(snapshot))
            emit({"phase": "session_save_complete", "global_step": step, "point_count": index})
            reason = session_stop_reason(step, target_step, elapsed(), stop_requested(), len(entries))
    if not entries:
        raise CheckpointError("training.session.no_updates")
    journal = {"format": 1, "purpose": "personal-session-candidate-journal", "request_sha256": request_sha,
        "start_global_step": start_step, "entries": entries, "stop_reason": reason}
    _write_new(root / "journal.json", _json(journal))
    return {"global_step": step, "stop_reason": reason, "entries": entries,
        "journal_sha256": hashlib.sha256(_json(journal)).hexdigest()}


def run_personal_session(request, request_sha, *, emit=lambda event: None):
    started = time.monotonic()
    from .personal_update import TrainingInputs, verify_admitted_training
    from .feature_corpus import CorpusRef, load_persisted_preview
    from .features import load_prepared_clips
    from .training_admission import TRAINING_PRESET
    from .update_checkpoints import load_update_checkpoint
    from .rvc_kernel import create_rvc_state, train_rvc_batch
    from .feature_batch import build_native_feature_batch
    fields = request["inputs"]
    ref = lambda value: CorpusRef(Path(value["root"]), value["sha256"])
    inputs = TrainingInputs(Path(fields["dataset_root"]), fields["dataset_sha256"],
        ref(fields["preflight"]), ref(fields["corpus"]), ref(fields["candidate"]), ref(fields["admission"]),
        Path(fields["source_root"]), Path(fields["base_asset_root"]))
    with ExitStack() as pins:
        root = plain_path(Path(request["output_root"]))
        pin_chain(pins, root)
        if validate_session_bootstrap(root):
            # No new update or publication: workflow may preserve the already
            # accepted predecessor only after observing this exact safe outcome.
            raise CheckpointError("training.session.no_updates")
        if shutil.disk_usage(root).free < FREE_GUARD_BYTES + MAX_POINTS * POINT_BYTES:
            raise CheckpointError("training.session.output_or_space.invalid")
        emit({"phase": "session_preparing"})
        admitted = pins.enter_context(verify_admitted_training(inputs))
        identity = admitted.run_identity(request["run_id"])
        previous = request["previous"]
        checkpoint = load_update_checkpoint(Path(previous["root"]), previous["sha256"], identity,
            admitted.train_ids, inputs.admission.sha256, TRAINING_PRESET["sampler_seed"])
        batches = {}
        expected_frames = {c["id"]: c["frames"] for c in admitted.decision["native_validation"]["clips"]}
        # Every batch is built before RNG restore. Release shard pins only after copies.
        for shard in admitted.corpus["shards"]:
            selected_ids = [i for i in shard["selected_clip_ids"] if i in admitted.train_ids]
            if not selected_ids: continue
            preview_ref = next(r for r in admitted.preview_refs if str(r.root) == shard["root"] and r.sha256 == shard["manifest_sha256"])
            with ExitStack() as shard_pins:
                selection = shard_pins.enter_context(load_prepared_clips(inputs.dataset_root, inputs.dataset_sha256, shard["selected_clip_ids"]))
                stored = shard_pins.enter_context(load_persisted_preview(preview_ref, selection))
                for clip_id in selected_ids:
                    prepared = next(c for c in selection.clips if c.id == clip_id)
                    clip = next(c for c in stored.clips if c.id == clip_id)
                    batch = build_native_feature_batch(inputs.source_root, prepared.audio40, clip.phone, clip.f0)
                    if int(batch.phone_lengths[0]) != expected_frames[clip_id]:
                        raise CheckpointError("training.session.frames.invalid")
                    batches[clip_id] = batch
                    emit({"phase": "session_batch_ready", "batch_count": len(batches), "total_batches": len(admitted.train_ids)})
        state = create_rvc_state(inputs.source_root, inputs.base_asset_root / "f0G40k.pth", inputs.base_asset_root / "f0D40k.pth")
        state.restore(checkpoint)  # RNG last; no extraction, random factory or reseeding after this.
        emit({"phase": "session_state_ready", "global_step": checkpoint.global_step, "epoch_size": len(admitted.train_ids)})
        def stopped():
            marker = root / "STOP"
            if not marker.exists(): return False
            with pinned_file(marker) as stream:
                if stream.seek(0, 2) > 64: raise CheckpointError("training.session.stop.invalid")
            return True
        result = run_session_updates(state, batches, admitted.train_ids, identity, inputs.admission.sha256, previous,
            root, request_sha, checkpoint.global_step, request["target_completed_epoch"] * len(admitted.train_ids),
            train_step=train_rvc_batch, emit=emit, stop_requested=stopped, elapsed=lambda: time.monotonic() - started)
        return {"phase": "personal_session_control", "request_sha256": request_sha,
            "journal_sha256": result["journal_sha256"], "global_step": result["global_step"],
            "completed_updates": result["global_step"] - checkpoint.global_step,
            "point_count": len(result["entries"]), "stop_reason": result["stop_reason"]}
