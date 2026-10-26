"""CPU training model backed by the admitted frozen FP32 file, never rebuilt."""
import contextlib
from collections import OrderedDict
import gc
import math
import os
import sys
from owned_files import open_file, io_path
from protocol import require, keys, safe_path, read_bytes, canonical_bytes
from frozen_layout import read_main_header, expected_adapter_layout, parameter_layout


@contextlib.contextmanager
def without_static_cache(minicpm_type):
    original, calls = minicpm_type.setup_cache, []
    def suppress(module, *args, **kwargs):
        module.kv_cache = None
        calls.append(module)
    minicpm_type.setup_cache = suppress
    try:
        yield calls
    finally:
        minicpm_type.setup_cache = original


def buffer_inventory(model, rope_type, lora_type):
    ropes, adapters, names = [], [], set()
    for name, module in model.named_modules():
        actual = {key for key in module._non_persistent_buffers_set if module._buffers.get(key) is not None}
        if isinstance(module, rope_type):
            require(actual == {"inv_freq", "cos_cached", "sin_cached"})
            ropes.append((name, module))
        elif isinstance(module, lora_type):
            require(actual == {"scaling"})
            adapters.append((name, module))
        else:
            require(not actual)
        names.update((name + "." if name else "") + key for key in actual)
    return ropes, adapters, names


def validate_copy_targets(values, layout):
    keys(values, (entry["name"] for entry in layout))


def tensor_finite(tensor, torch):
    if not tensor.is_floating_point():
        return True
    flat = tensor.detach().reshape(-1)
    return all(bool(torch.isfinite(flat[i:i + 262144]).all()) for i in range(0, flat.numel(), 262144))


def checked_vae_state(archive, expected_names):
    state = archive.get("state_dict", archive)
    require(type(state) in (dict, OrderedDict) and set(state) == expected_names)
    return state


def load_training_model(request, phase):
    import ctypes
    import torch
    from transformers import LlamaTokenizerFast
    from torch.nn.utils.weight_norm import WeightNorm
    from voxcpm.model.voxcpm2 import VoxCPM2Model, VoxCPMConfig, LoRAConfig
    from voxcpm.modules.audiovae import AudioVAEV2
    from voxcpm.modules.layers.lora import LoRALinear
    from voxcpm.modules.minicpm4.model import MiniCPMModel, MiniCPMLongRoPE
    from trainer import recipe

    require(os.name == "nt" and sys.byteorder == "little" and ctypes.sizeof(ctypes.c_void_p) == 8)
    require(torch.get_default_dtype() == torch.float32)
    root = request["modelRoot"]
    main = request["modelFiles"]["model.safetensors"]
    header = read_main_header(safe_path(root, "model.safetensors"), main["bytes"])
    layout = expected_adapter_layout(header)
    require(canonical_bytes(layout) == canonical_bytes(request["parameterLayout"]))
    require(len(layout) == 384 and sum(math.prod(entry["shape"]) for entry in layout) == 1130496)
    config = VoxCPMConfig.model_validate_json(read_bytes(safe_path(root, "config.json"), 1024 * 1024))
    config.dtype, config.device = "float32", "cpu"
    require(config.audio_vae_config is not None)
    lora = LoRAConfig(**recipe()["lora"])
    require(lora.model_dump() == recipe()["lora"])
    tokenizer = LlamaTokenizerFast.from_pretrained(root, local_files_only=True)
    with without_static_cache(MiniCPMModel) as suppressed:
        with torch.device("meta"):
            vae = AudioVAEV2(config=config.audio_vae_config)
            model = VoxCPM2Model(config, tokenizer, vae, lora_config=lora, device="cpu")
    require(len(suppressed) == 2 and all(module.kv_cache is None for module in suppressed))
    parameters = list(model.named_parameters())
    require(all(p.is_meta and p.dtype == torch.float32 for _, p in parameters))
    adapter_names = {entry["name"] for entry in layout}
    actual_adapters = sorted((dict(name=n, shape=list(p.shape), dtype="F32") for n, p in parameters if n.endswith((".lora_A", ".lora_B"))), key=lambda e: e["name"])
    require(canonical_bytes(actual_adapters) == canonical_bytes(layout))
    ropes, adapters, nonpersistent = buffer_inventory(model, MiniCPMLongRoPE, LoRALinear)
    require(len(ropes) == 3 and len(adapters) == 192)
    state = model.state_dict()
    require(set(header) == {name for name in state if not name.startswith("audio_vae.")} - adapter_names)
    for name, entry in header.items():
        require(list(state[name].shape) == entry["shape"])
    vae_keys = {name.removeprefix("audio_vae.") for name in state if name.startswith("audio_vae.")}
    persistent = {name for name, _ in model.named_buffers() if name not in nonpersistent}
    require(persistent == {"audio_vae.decoder.sr_bin_boundaries"})
    frozen_layout, frozen_count, layout_sha = parameter_layout(parameters, adapter_names)
    require(frozen_count * 4 == request["cache"]["bytes"] and layout_sha == request["cache"]["layoutSha256"])
    # Share RW for Torch's writable shared mapping, deny DELETE. Parent owns the
    # cross-process exclusive lease plus full pre/post SHA and resource Job.
    cache_pin = open_file(request["cache"]["path"], mutable=True)
    cache_stream = cache_pin.__enter__()
    try:
        require(os.fstat(cache_stream.fileno()).st_size == request["cache"]["bytes"])
        backing = torch.from_file(str(io_path(request["cache"]["path"])), shared=True, size=frozen_count, dtype=torch.float32, device="cpu")
        require(backing.numel() == frozen_count and backing.is_contiguous())
        cursor = 0
        for name, old in parameters:
            owner_path, _, leaf = name.rpartition(".")
            owner = model.get_submodule(owner_path) if owner_path else model
            if name in adapter_names:
                value = torch.empty(tuple(old.shape), dtype=torch.float32, device="cpu")
            else:
                value = backing[cursor:cursor + old.numel()].view(tuple(old.shape))
                cursor += old.numel()
            owner._parameters[leaf] = torch.nn.Parameter(value, requires_grad=name in adapter_names)
        require(cursor == frozen_count)
        for _, module in model.named_modules():
            for name, old in list(module._buffers.items()):
                if old is not None:
                    require(old.numel() * old.element_size() <= 64 * 1024**2)
                    module._buffers[name] = torch.empty(0 if name in module._non_persistent_buffers_set else tuple(old.shape), dtype=old.dtype, device="cpu")
        del state, parameters, old, value
        destinations = model.state_dict()
        frozen_names = {name for name, _ in model.named_parameters()} - adapter_names
        require(set(destinations) == frozen_names | adapter_names | persistent)
        vae_entry = request["modelFiles"]["audiovae.pth"]
        require(vae_entry["bytes"] == 376951122)
        # A separate pinned base asset, not a small optimizer checkpoint. The
        # resource Job bounds this weights_only load; it never loads main weights.
        with open_file(safe_path(root, "audiovae.pth")) as stream:
            require(os.fstat(stream.fileno()).st_size == vae_entry["bytes"])
            archive = torch.load(stream, map_location="cpu", weights_only=True, mmap=False)
        vae_state = checked_vae_state(archive, vae_keys)
        require(all(isinstance(tensor, torch.Tensor) for tensor in vae_state.values()))
        require(sum(t.numel() * t.element_size() for t in vae_state.values()) <= 512 * 1024**2)
        with torch.no_grad():
            for name, source in vae_state.items():
                full_name = "audio_vae." + name
                target = destinations[full_name]
                require(source.device.type == "cpu" and not source.is_meta and source.shape == target.shape)
                require(source.is_floating_point() == target.is_floating_point())
                require(source.dtype in (torch.float32, torch.float16, torch.bfloat16) if target.is_floating_point() else source.dtype == target.dtype)
                if full_name in persistent:
                    require(source.dtype == torch.int32 and tuple(source.shape) == (3,))
                    require(torch.equal(source, torch.tensor([20000, 30000, 40000], dtype=torch.int32)))
                    target.copy_(source)
                else:
                    require(full_name in frozen_names)
                    # Intentionally no copy into the writable frozen mapping.
        del vae_state, archive, source, target
        gc.collect()
        with torch.no_grad():
            for _, module in adapters:
                torch.nn.init.kaiming_uniform_(module.lora_A, a=math.sqrt(5))
                torch.nn.init.zeros_(module.lora_B)
                module.scaling = torch.tensor(module._base_scaling, dtype=torch.float32, device="cpu")
            for _, rope in ropes:
                rope.inv_freq = 1.0 / (rope.base ** (torch.arange(0, rope.dim, 2, device="cpu", dtype=torch.float32) / rope.dim))
                rope._set_cos_sin_cache(seq_len=rope.max_position_embeddings, device=torch.device("cpu"), dtype=torch.float32)
            normalized = 0
            for module in model.modules():
                for hook in module._forward_pre_hooks.values():
                    if isinstance(hook, WeightNorm):
                        require(hook.name == "weight" and hook.dim == 0)
                        setattr(module, hook.name, hook.compute_weight(module))
                        require(tensor_finite(getattr(module, hook.name), torch))
                        normalized += 1
        require(normalized > 0)
        for name, offset_shape, offset, count in frozen_layout:
            p = model.get_parameter(name)
            require(p.untyped_storage().data_ptr() == backing.untyped_storage().data_ptr() and p.storage_offset() == offset and list(p.shape) == offset_shape)
        for name, p in model.named_parameters():
            require(not p.is_meta and p.device.type == "cpu" and p.dtype == torch.float32 and p.requires_grad == (name in adapter_names))
            require(name not in adapter_names or tensor_finite(p, torch))
        actual_nonpersistent = set()
        for name, module in model.named_modules():
            for key, tensor in module._buffers.items():
                if tensor is not None:
                    require(not tensor.is_meta and tensor.device.type == "cpu" and tensor_finite(tensor, torch))
                    require(not tensor.is_floating_point() or tensor.dtype == torch.float32)
                    if key in module._non_persistent_buffers_set:
                        actual_nonpersistent.add((name + "." if name else "") + key)
            for attribute, tensor in vars(module).items():
                if isinstance(tensor, torch.Tensor):
                    require(attribute == "weight" and hasattr(module, "weight_g") and hasattr(module, "weight_v"))
                    require(not tensor.is_meta and tensor.device.type == "cpu" and tensor.dtype == torch.float32)
        require(actual_nonpersistent == nonpersistent and all(m.kv_cache is None for m in model.modules() if isinstance(m, MiniCPMModel)))
        model.train()
        model.audio_vae.eval()
        model._training_backing = backing
        model._training_cache_pin = cache_pin
        model._training_cache_stream = cache_stream
        return model
    except BaseException:
        cache_pin.__exit__(*sys.exc_info())
        raise
