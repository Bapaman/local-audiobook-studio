using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmInferenceReferenceProtocolTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Whole_reference_is_captured_pinned_and_accepted_with_derived_cache(bool longPath)
    {
        using var f = new VoxInferenceFixture(longPath); string path = f.UseReference();
        if (longPath) Assert.True(path.Length > 260);
        using (var pins = new VoxCpmTrainingFilePins())
        {
            var call = await f.Create(pins); using var request = JsonDocument.Parse(call.RequestBytes);
            var root = request.RootElement; var reference = root.GetProperty("reference");
            Assert.Equal(14,root.EnumerateObject().Count()); Assert.Equal(2,root.GetProperty("format").GetInt32());
            Assert.Equal(VoxCpmInferenceProfile.ReferenceId,root.GetProperty("profileId").GetString());
            Assert.Equal(10,reference.EnumerateObject().Count()); Assert.Equal(201680,reference.GetProperty("samples").GetInt32());
            Assert.Equal(403404,reference.GetProperty("bytes").GetInt32());
            Assert.Equal(f.Execution.Voice.Binding.Reference!.AudioSha256,reference.GetProperty("sha256").GetString());
            Assert.Throws<IOException>(()=>File.Move(path,path+".moved"));
            Assert.Throws<IOException>(()=>File.Open(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite));
            f.Publish(call);
            Assert.Equal(100,(await VoxCpmInferenceProtocol.AcceptAsync(call,TestContext.Current.CancellationToken)).SampleCount);
        }
        File.Move(path,path+".moved"); File.Move(path+".moved",path);
    }

    [Theory]
    [InlineData("reference-sha")][InlineData("reference-id")][InlineData("reference-extra")]
    [InlineData("reference-patches")][InlineData("reference-prefix")][InlineData("reference-profile")]
    [InlineData("reference-missing")][InlineData("cache")][InlineData("capacity")]
    public async Task Forged_reference_proof_is_rejected(string fault)
    {
        using var f = new VoxInferenceFixture(); f.UseReference(); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins); f.Publish(call,fault);
        await Assert.ThrowsAsync<WorkerClientException>(()=>VoxCpmInferenceProtocol.AcceptAsync(call,TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("member")][InlineData("profile")][InlineData("rate")][InlineData("channels")][InlineData("frames")]
    public async Task Invalid_member_or_canonical_wave_is_rejected_before_launch(string fault)
    {
        using var f = new VoxInferenceFixture(); f.UseReference(fault); using var pins = new VoxCpmTrainingFilePins();
        await Assert.ThrowsAsync<WorkerClientException>(()=>f.Create(pins));
    }
}
