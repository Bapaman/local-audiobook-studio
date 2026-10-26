using System.Text;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusModelLockTests
{
    [Fact]
    public void Public_lock_admits_exact_eleven_files_and_pinned_whisper_weights()
    {
        var files = TtsCorpusRuntimeInventory.ValidateModelLock(ReadLock());
        Assert.Equal(11, files.Count);
        var weights = Assert.Single(files, f => f.Filename == "model.safetensors");
        Assert.Equal(1617824864, weights.Bytes);
        Assert.Equal("542566a422ae4f3fd23f1ba11add198fca01bbf82e66e6a2857b3f608b1eb9d1", weights.Sha256);
    }
    [Fact]
    public void Arbitrary_self_consistent_local_model_manifest_is_not_admitted()
    {
        string text = Encoding.UTF8.GetString(ReadLock()).Replace("41f01f3fe87f28c78e2fbf8b568835947dd65ed9", new string('0', 40), StringComparison.Ordinal);
        Assert.Throws<TtsCorpusExecutionException>(() => TtsCorpusRuntimeInventory.ValidateModelLock(Encoding.UTF8.GetBytes(text)));
    }
    private static byte[] ReadLock() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "locks", "whisper-large-v3-turbo.lock.json"));
}
