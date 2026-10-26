import unittest
import fixtures
import protocol


class CursorTests(unittest.TestCase):
    def test_cursor_gaps_and_epoch_rollover(self):
        value = fixtures.request()
        self.assertEqual(protocol.advance_cursor(value["cursor"], value["clipIds"]),
                         dict(globalStep=2, completedEpochs=1, epochSize=2, nextRetainedOrdinal=0, nextClipId="9" * 32))
        for bad in (dict(value["cursor"], globalStep=True), dict(value["cursor"], globalStep=2), dict(value["cursor"], nextClipId="9" * 32)):
            with self.subTest(bad=bad), self.assertRaises(protocol.ProtocolError):
                protocol.advance_cursor(bad, value["clipIds"])
