"""One complete CPU LoRA update. Optional tensor imports are strictly lazy."""
import copy
import math
from pathlib import Path
import random
import struct
from owned_files import exists_plain_file
from protocol import (require, keys, integer, finite, canonical_bytes, sha256, safe_path, execution_root,
                      read_bytes, validate_request, advance_cursor)
from mapped_model import tensor_finite


def recipe():
    return dict(format=1, device="cpu", dtype="float32", batchSize=1, workers=0, clipsPerUpdate=1, seed=42,
                parameterOrder="ordinal-ascii-name", resample=False, truncate=False, shuffle=False,
                packer=dict(datasetCount=1, taskId=1, datasetId=0, isPrompt=False),
                forward=dict(progress=0.0, sampleGenerate=False),
                lora=dict(enable_lm=True, enable_dit=True, enable_proj=False, r=2, alpha=2, dropout=0.0,
                          target_modules_lm=["q_proj", "v_proj", "k_proj", "o_proj"], target_modules_dit=["q_proj", "v_proj", "k_proj", "o_proj"],
                          target_proj_modules=["enc_to_lm_proj", "lm_to_dit_proj", "res_to_dit_proj", "fusion_concat_proj"]),
                optimizer=dict(lr=0.0001, weight_decay=0.01, betas=[0.9, 0.999], eps=1e-8, foreach=False,
                               amsgrad=False, maximize=False, capturable=False, differentiable=False, fused=None, decoupled_weight_decay=True),
                scheduler=dict(name="StepLR", stepSize=100, gamma=0.99), gradientClip=dict(maxNorm=1.0, foreach=False))


def ordered_adapters(model, layout):
    parameters = dict(model.named_parameters())
    expected = {entry["name"] for entry in layout}
    require({name for name, p in parameters.items() if p.requires_grad} == expected)
    result = []
    for entry in layout:
        p = parameters[entry["name"]]
        require(list(p.shape) == entry["shape"])
        result.append((entry["name"], p))
    require([name for name, _ in result] == sorted(expected))
    return result


def packed_length(token_count, samples, hop_length, patch_size, max_length, rope_capacity):
    integer(token_count, 1, 16000)
    integer(samples, 1, 256000)
    patch = integer(hop_length, 1, 16000) * integer(patch_size, 1, 64)
    patches = (samples + patch - 1) // patch
    full = token_count + patches + 2
    require(full <= min(integer(max_length, 1, 65536), integer(rope_capacity, 1, 65536)))
    return full, patches


def read_training_pcm(request):
    clip = request["selectedClip"]
    count = clip["endSample"] - clip["startSample"]
    payload = read_bytes(safe_path(request["projectRoot"], clip["audioRelativePath"]), 44 + 256000 * 2)
    require(len(payload) == 44 + count * 2 and sha256(payload) == clip["audioSha256"])
    expected_header = b"RIFF" + struct.pack("<I", 36 + count * 2) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, 16000, 32000, 2, 16) + b"data" + struct.pack("<I", count * 2)
    require(payload[:44] == expected_header)
    # No resampler, denoise, loudness/pitch processing or sample slicing.
    return payload[44:]


def apply_update(losses, named, optimizer, scheduler, torch, stopped, next_step):
    parameters = [p for _, p in named]
    for key in ("loss/diff", "loss/stop"):
        require(tensor_finite(losses[key], torch))
    total = losses["loss/diff"] + losses["loss/stop"]
    require(tensor_finite(total, torch))
    before = [p.detach().clone() for p in parameters]
    total.backward()
    require(any(p.grad is not None for p in parameters))
    require(all(p.grad is None or tensor_finite(p.grad, torch) for p in parameters))
    norm = torch.nn.utils.clip_grad_norm_(parameters, 1.0, foreach=False)
    require(tensor_finite(norm, torch))
    if stopped():
        return None
    optimizer.step()
    scheduler.step()
    require(scheduler.last_epoch == next_step and optimizer.state)
    require(all(tensor_finite(p, torch) for p in parameters))
    require(any(not torch.equal(old, p.detach()) for old, p in zip(before, parameters)))
    for parameter, state in optimizer.state.items():
        require(any(parameter is p for p in parameters))
        keys(state, ("step", "exp_avg", "exp_avg_sq"))
        require(all(tensor_finite(value, torch) for value in state.values()))
    optimizer.zero_grad(set_to_none=True)
    return dict(loss=finite(float(total.detach())), gradientNorm=finite(float(norm), 0))


class _TorchBackend:
    def __init__(self, request, phase):
        import numpy as np
        import torch
        from mapped_model import load_training_model
        require(torch.get_default_dtype() == torch.float32 and not torch.cuda.is_available())
        torch.set_num_threads(2)
        torch.set_num_interop_threads(1)
        require(torch.get_num_threads() == 2 and torch.get_num_interop_threads() == 1)
        require(request["identity"]["recipeSha256"] == sha256(canonical_bytes(recipe())))
        random.seed(42)
        np.random.seed(42)
        torch.manual_seed(42)
        self.request, self.torch, self.np = request, torch, np
        self.model = load_training_model(request, phase)
        self.named = ordered_adapters(self.model, request["parameterLayout"])
        settings = recipe()["optimizer"]
        settings["betas"] = tuple(settings["betas"])
        # AdamW already decouples decay; older/newer signatures need not accept
        # that effective-state-only key as a constructor argument.
        del settings["decoupled_weight_decay"]
        self.optimizer = torch.optim.AdamW([p for _, p in self.named], **settings)
        self.scheduler = torch.optim.lr_scheduler.StepLR(self.optimizer, step_size=100, gamma=0.99)
        require(self.scheduler.last_epoch == 0)

    def restore(self, root):
        from checkpoint import restore_training_state
        restore_training_state(self.model, self.optimizer, self.scheduler, root, dict(request=self.request))

    def pack(self, pcm, text):
        from voxcpm.training.packers import AudioFeatureProcessingPacker
        model, torch, np = self.model, self.torch, self.np
        require(model.audio_vae.sample_rate == 16000 and not model.feat_decoder.mean_mode)
        tokens = model.text_tokenizer(text)
        require(type(tokens) is list and all(type(t) is int and t >= 0 for t in tokens))
        length, patches = packed_length(len(tokens), len(pcm) // 2, int(model.audio_vae.hop_length), model.config.patch_size,
                                        model.config.max_length, model.config.lm_config.max_position_embeddings)
        audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32)
        audio /= 32768.0
        require(len(audio) == len(pcm) // 2 and np.isfinite(audio).all())
        packer = AudioFeatureProcessingPacker(dataset_cnt=1, max_len=model.config.max_length,
                                              patch_size=model.config.patch_size, feat_dim=model.config.feat_dim, audio_vae=model.audio_vae)
        batch = packer(audio_tokens=torch.from_numpy(audio).unsqueeze(0), text_tokens=torch.tensor([tokens], dtype=torch.int32),
                       task_ids=torch.tensor([1], dtype=torch.int32), dataset_ids=torch.tensor([0], dtype=torch.int32), is_prompts=[False])
        require(batch["text_tokens"].shape == (1, length) and batch["audio_feats"].shape == (1, length, model.config.patch_size, model.config.feat_dim))
        for key in ("text_mask", "audio_mask", "loss_mask", "position_ids", "labels"):
            require(batch[key].shape == (1, length))
        require(int(batch["loss_mask"].sum()) == patches and int(batch["labels"].sum()) == 1 and int(batch["labels"][0, -2]) == 1)
        require(tensor_finite(batch["audio_feats"], torch))
        return batch

    def update(self, batch, stopped):
        self.optimizer.zero_grad(set_to_none=True)
        allowed = ("text_tokens", "text_mask", "audio_feats", "audio_mask", "loss_mask", "position_ids", "labels")
        losses = self.model(**{key: batch[key] for key in allowed}, progress=0.0, sample_generate=False)
        return apply_update(losses, self.named, self.optimizer, self.scheduler, self.torch, stopped, self.request["cursor"]["globalStep"] + 1)

    def save(self):
        from checkpoint import save_training_state
        return save_training_state(self.model, self.optimizer, self.scheduler,
                                   safe_path(self.request["projectRoot"], self.request["outputRelativeRoot"]), dict(request=self.request))


def _create_backend(request, phase):
    from runtime import install_dependencies
    install_dependencies(request)
    return _TorchBackend(request, phase)


def train_one(request, phase):
    validate_request(request)
    stop = safe_path(request["projectRoot"], execution_root(request) + "/STOP")
    stopped = lambda: exists_plain_file(stop)
    if stopped():
        return None
    backend = _create_backend(request, phase)
    if request["parentCheckpoint"] is not None:
        root = safe_path(request["projectRoot"], request["parentCheckpoint"]["manifestRelativePath"]).parent
        backend.restore(root)
    if stopped():
        return None
    pcm = read_training_pcm(request)
    batch = backend.pack(pcm, request["selectedClip"]["correctedText"])
    if stopped():
        return None
    phase("training")
    metrics = backend.update(batch, stopped)
    if metrics is None:
        require(stopped())
        return None
    keys(metrics, ("loss", "gradientNorm"))
    finite(metrics["loss"])
    finite(metrics["gradientNorm"], 0)
    phase("checkpointing")
    payloads = backend.save()
    return dict(payloads=payloads, metrics=metrics)
