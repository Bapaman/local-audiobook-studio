"""Explicit doubles for the unavailable/expensive tensor and model boundary."""
import contextlib
import math
import types


class Tensor:
    def __init__(self, values, shape=None, dtype='float32', device='cpu'):
        self.values = list(values)
        self.shape = tuple(shape) if shape is not None else (len(values),)
        self.dtype = dtype
        self.device = types.SimpleNamespace(type=device)
        self.requires_grad = False

    def numel(self):
        return math.prod(self.shape)

    def element_size(self):
        return 4

    def detach(self):
        return self

    def abs(self):
        return Tensor([abs(v) for v in self.values], self.shape)

    def max(self):
        return Tensor([max(self.values)], ())

    def all(self):
        return Tensor([all(self.values)], ())

    def item(self):
        if len(self.values) != 1:
            raise AssertionError('not scalar')
        return self.values[0]

    def argmax(self, dim):
        if dim != -1 or self.shape != (1, 2):
            raise AssertionError('bad logits')
        return Tensor([self.values.index(max(self.values))], (1,))

    def __getitem__(self, index):
        return Tensor([self.values[index]], ())

    def copy_(self, source):
        self.values = list(source.values)

    def requires_grad_(self, value):
        self.requires_grad = value
        return self


class Torch:
    Tensor = Tensor
    float32 = 'float32'

    def __init__(self):
        self.inference_active = False

    @contextlib.contextmanager
    def inference_mode(self):
        self.inference_active = True
        try:
            yield
        finally:
            self.inference_active = False

    no_grad = inference_mode

    @staticmethod
    def isfinite(tensor):
        return Tensor([math.isfinite(v) for v in tensor.values], tensor.shape)

    @staticmethod
    def equal(left, right):
        return left.shape == right.shape and left.values == right.values


def lm_config(layers=2):
    return types.SimpleNamespace(num_hidden_layers=layers, num_key_value_heads=2,
                                 hidden_size=8, num_attention_heads=2, kv_channels=4,
                                 max_position_embeddings=8192)


class LM:
    def __init__(self):
        self.config = lm_config()
        self.kv_cache = None
        self.allocations = []

    def setup_cache(self, batch_size, max_length, device, dtype):
        self.allocations.append((batch_size, max_length, device, dtype))
        c = self.config
        shape = (2, c.num_hidden_layers, batch_size, c.num_key_value_heads,
                 max_length, c.kv_channels)
        self.kv_cache = types.SimpleNamespace(max_length=max_length,
                                              kv_cache=Tensor([0], shape, dtype, device))


class StopHead:
    def __init__(self):
        self.hook = None

    def register_forward_hook(self, callback):
        if self.hook is not None:
            raise AssertionError('hook leaked')
        self.hook = callback
        return types.SimpleNamespace(remove=lambda: setattr(self, 'hook', None))


class Model:
    def __init__(self, torch):
        self.torch = torch
        self.base_lm, self.residual_lm = LM(), LM()
        self.config = types.SimpleNamespace(max_length=8192)
        self.sample_rate, self.patch_size, self._decode_chunk_size = 48000, 4, 1920
        self.training = False
        self.stop_head = StopHead()
        self.tokenizer_inputs = []
        self.text_tokenizer = self.tokenize
        self.flags = [0, 0, 0, 1]
        self.waveform = Tensor([0.0, 0.5, -0.25, 0.0], (1, 4))
        self.arguments = None
        self.drift_text = None
        self.skip_tokenizer = False
        self.failure = False
        self.bad_logits = False

    def tokenize(self, text):
        self.tokenizer_inputs.append(text)
        return [1, 2, 3, 4, 5]

    def parameters(self):
        return []

    def eval(self):
        self.training = False
        return self

    def generate(self, **kwargs):
        if not self.torch.inference_active:
            raise AssertionError('inference context missing')
        self.arguments = kwargs
        if not self.skip_tokenizer:
            for _ in range(2):
                text = self.drift_text if self.drift_text is not None else kwargs['target_text']
                self.text_tokenizer(text)
        if self.failure:
            raise RuntimeError('synthetic generation failure')
        for flag in self.flags:
            output = Tensor([1 - flag, flag], (1, 2))
            if self.bad_logits:
                output.values[0] = float('nan')
            self.stop_head.hook(self.stop_head, (), output)
        return self.waveform
