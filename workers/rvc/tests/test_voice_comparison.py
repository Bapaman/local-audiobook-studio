"""One bounded context path, not an auditory quality score or trained-voice proof."""
import sys
from pathlib import Path
import unittest
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from las_rvc.checkpoints import CheckpointError
try:
    from las_rvc.voice_comparison import prepare_context_audio, align_context_features, trim_context_audio
except ImportError:
    prepare_context_audio = align_context_features = trim_context_audio = None


class VoiceContextTests(unittest.TestCase):
    def test_reflected_context_does_not_modify_or_filter_source(self):
        self.assertIsNotNone(prepare_context_audio, "Inference still lacks reflected acoustic context")
        audio = (np.sin(np.arange(6400) * .13) * .1 + .05).astype(np.float32)
        before = audio.copy()
        padded = prepare_context_audio(audio)
        np.testing.assert_array_equal(audio, before)
        self.assertEqual((38400,), padded.shape)
        self.assertEqual(np.float32, padded.dtype)
        np.testing.assert_array_equal(padded[16000:22400], before)
        np.testing.assert_array_equal(padded[15999:9599:-1], padded[16001:22401])

    def test_reextract_align_and_trim_preserve_the_same_central_duration(self):
        self.assertIsNotNone(align_context_features, "Padded conversion alignment is not implemented")
        # 0.4s + 2*1s -> 119 HuBERT frames -> 238 RVC frames;
        # removing 100 frames at each edge leaves 38 frames / 15200 samples.
        padded = np.full(38400, .1, np.float32)
        phone = np.zeros((119, 768), np.float32)
        phone[:, 0] = np.arange(119)
        f0 = np.full(241, 220, np.float32)
        f0[:3] = [0, 50, 1100]
        aligned = align_context_features(padded, phone, f0)
        self.assertEqual((238, 768), aligned.phone.shape)
        np.testing.assert_array_equal(aligned.phone[:4, 0], [0, 0, 1, 1])
        np.testing.assert_array_equal(aligned.pitch[:3], [1, 1, 255])
        np.testing.assert_array_equal(aligned.pitchf[:3], [50, 50, 1100])
        generated = np.arange(95200, dtype=np.float32) / np.float32(100000)
        central = trim_context_audio(generated, 238, 15200)
        np.testing.assert_array_equal(central, generated[40000:55200])
        for invalid in (generated[:-1], generated + np.nan):
            with self.assertRaises(CheckpointError): trim_context_audio(invalid, 238, 15200)
        with self.assertRaises(CheckpointError): trim_context_audio(generated, 238, 15600)

    def test_invalid_or_unvoiced_inputs_cannot_be_padded_into_a_fake_result(self):
        self.assertIsNotNone(prepare_context_audio)
        for audio in (np.zeros(6400, np.float64), np.full(6400, np.nan, np.float32),
                np.zeros(6399, np.float32), np.zeros(59201, np.float32)):
            with self.assertRaises(CheckpointError): prepare_context_audio(audio)
        padded = np.full(38400, .1, np.float32)
        phone = np.zeros((119, 768), np.float32)
        for f0 in (np.zeros(241, np.float32), np.ones(240, np.float32), np.full(241, 1101, np.float32)):
            with self.assertRaises(CheckpointError): align_context_features(padded, phone, f0)


if __name__ == "__main__": unittest.main()
