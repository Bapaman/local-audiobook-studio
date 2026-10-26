using System.Security.Cryptography;
using LocalAudiobookStudio.App.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusWorkerPinTests
{
    [Fact]
    public void All_shipped_worker_bytes_match_actual_runtime_admission_pins()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "TtsCorpusWorker");
        string[] paths = Directory.GetFiles(root);
        Assert.Equal(new[] { "asr.py", "prepare.py", "protocol.py", "worker.py" },
            paths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(4, TtsCorpusRuntimeInventory.Scripts.Count);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            Assert.True(TtsCorpusRuntimeInventory.Scripts.TryGetValue(name, out string? pin), name);
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.True(pin == actual, $"{name}: admission pin {pin}, shipped bytes {actual}");
        }
    }
}
