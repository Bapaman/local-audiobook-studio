"""Own epoch boundaries; no stock RVC implicit checkpoint fallback or exit."""
from dataclasses import dataclass
from typing import Callable

import torch

from .checkpoints import Checkpoint, CheckpointError, CheckpointStore
from .training_state import TrainingState


class EpochCanceled(Exception):
    """The batch callback observed should_cancel() and discarded its epoch."""


@dataclass(frozen=True)
class TrainingResult:
    status: str
    completed_epoch: int
    global_step: int
    checkpoint: Checkpoint | None


def train_epochs(state: TrainingState, store: CheckpointStore,
                 train_epoch: Callable[[int, int, Callable[[], bool]], int],
                 total_epochs: int, control: Callable[[], str], report: Callable[[dict], None],
                 checkpoint_interval: int = 20) -> TrainingResult:
    if (type(total_epochs) is not int or not 1 <= total_epochs <= 200
            or type(checkpoint_interval) is not int or not 1 <= checkpoint_interval <= 20):
        raise CheckpointError("training.preset.invalid")
    torch.set_num_threads(2)
    point = store.latest()
    position = state.restore(point) if point is not None else None
    completed = point.completed_epoch if point is not None else 0
    step = position.global_step if position is not None else 0
    if completed > total_epochs:
        raise CheckpointError("training.resume.incompatible")

    def requested():
        value = control()
        if value not in ("run", "pause", "cancel"):
            raise CheckpointError("training.control.invalid")
        return value

    def result(status):
        report({"phase": status, "completed": completed, "total": total_epochs})
        return TrainingResult(status, completed, step, point)

    for epoch in range(completed + 1, total_epochs + 1):
        intent = requested()
        if intent == "cancel":
            return result("canceled")
        if intent == "pause":
            if completed > 0 and (point is None or point.completed_epoch < completed):
                report({"phase": "checkpoint", "completed": completed, "total": total_epochs})
                point = state.save(store, completed, step)
            return result("paused")
        pause_seen = False

        def should_cancel():
            nonlocal pause_seen
            intent = requested()
            pause_seen |= intent == "pause"
            return intent == "cancel"

        report({"phase": "training", "completed": completed, "total": total_epochs})
        try:
            next_step = train_epoch(epoch, step, should_cancel)
            if requested() == "cancel":
                return result("canceled")
            if type(next_step) is not int or next_step <= step:
                raise ValueError("epoch must perform optimizer updates")
            state.scheduler_g.step()
            state.scheduler_d.step()
        except EpochCanceled:
            if requested() != "cancel":
                raise CheckpointError("training.epoch.failed") from None
            return result("canceled")
        except CheckpointError:
            raise
        except Exception:
            raise CheckpointError("training.epoch.failed") from None
        completed, step = epoch, next_step
        pause_seen |= requested() == "pause"
        # Publication is AFTER both scheduler steps, including the final epoch.
        if completed % checkpoint_interval == 0 or completed == total_epochs or pause_seen:
            report({"phase": "checkpoint", "completed": completed, "total": total_epochs})
            point = state.save(store, completed, step)
        report({"phase": "training", "completed": completed, "total": total_epochs})
        if pause_seen:
            return result("paused")
    return result("completed")
