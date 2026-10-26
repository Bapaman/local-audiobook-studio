using System.Security.Cryptography;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusFilePinsTests
{
    [Fact]
    public async Task Pin_verifies_bytes_and_prevents_replacement_or_writing_until_disposed()
    {
        using var temporary = new CorpusTemporaryDirectory(); string path = temporary.Child("asset.bin"); File.WriteAllBytes(path, [1, 2, 3]);
        using (var pins = new TtsCorpusFilePins())
        {
            var stream = await pins.ReadAsync(path, 3, "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", TestContext.Current.CancellationToken);
            Assert.Equal(3, stream.Length);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9, 9, 9]));
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        }
        File.WriteAllBytes(path, [9, 9, 9]); Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task Oversized_or_tampered_asset_is_not_admitted(long maximumBytes, bool wrongHash)
    {
        using var temporary = new CorpusTemporaryDirectory(); string path = temporary.Child("asset.bin"); File.WriteAllBytes(path, [1, 2, 3]);
        using var pins = new TtsCorpusFilePins();
        string sha = wrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => pins.ReadAsync(path, maximumBytes, sha, TestContext.Current.CancellationToken));
    }
}
