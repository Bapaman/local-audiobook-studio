using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmInferenceControlsProtocolTests
{
    [Theory]
    [InlineData(true,80)][InlineData(false,90)][InlineData(true,100)][InlineData(false,120)]
    public async Task Captured_controls_reach_request_and_raw_and_final_are_independently_accepted(bool reference,int tempo)
    {
        using var f = new VoxInferenceFixture();
        f.UseOptions(new(731,8,1.5,tempo,reference)); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins); using var document = JsonDocument.Parse(call.RequestBytes); var q = document.RootElement;
        Assert.Equal(15,q.EnumerateObject().Count()); Assert.Equal(3,q.GetProperty("format").GetInt32());
        Assert.Equal(VoxCpmInferenceProfile.ControlsId,q.GetProperty("profileId").GetString());
        var controls = q.GetProperty("controls"); Assert.Equal(6,controls.EnumerateObject().Count());
        Assert.Equal(731,controls.GetProperty("seed").GetInt64()); Assert.Equal(8,controls.GetProperty("timesteps").GetInt32());
        Assert.Equal(1.5,controls.GetProperty("cfg").GetDouble()); Assert.Equal(tempo,controls.GetProperty("tempoPercent").GetInt32());
        Assert.Equal(reference,controls.GetProperty("useReference").GetBoolean());
        Assert.Equal(reference ? JsonValueKind.Object : JsonValueKind.Null,q.GetProperty("reference").ValueKind);
        f.Publish(call); var candidate = await VoxCpmInferenceProtocol.AcceptAsync(call,TestContext.Current.CancellationToken);
        Assert.Equal(decimal.Round(10000m/tempo,0,MidpointRounding.ToEven),(decimal)candidate.SampleCount);
    }

    [Theory]
    [InlineData("controls-echo")][InlineData("controls-extra")][InlineData("tempo-rate")][InlineData("tempo-algorithm")]
    [InlineData("tempo-samples")][InlineData("raw-hash")][InlineData("raw-samples")][InlineData("raw-extra")]
    [InlineData("raw-missing")][InlineData("identity-mismatch")][InlineData("reference-sha")][InlineData("cache")]
    public async Task Forged_processing_or_raw_proof_is_rejected(string fault)
    {
        using var f = new VoxInferenceFixture(); f.UseOptions(new()); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins); f.Publish(call,fault);
        await Assert.ThrowsAsync<WorkerClientException>(()=>VoxCpmInferenceProtocol.AcceptAsync(call,TestContext.Current.CancellationToken));
    }
}
