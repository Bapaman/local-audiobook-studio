using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public class SavedVoiceEnqueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Saved_selection_requires_consent_and_freezes_binding_before_later_training_changes()
    {
        using var box = new CatalogSandbox(); var prepared = await StudioJobServiceTests.Prepare(box);
        string marker = Path.Combine(box.Root, "runtime.txt"); await File.WriteAllTextAsync(marker, "inert", Token);
        var training = new TrainingRuntime(new(marker, marker, box.Root, box.Root, box.Root, box.Root, box.Root), new TrainingRuntimeTests.InertInspector(marker));
        var resolver = new Resolver(); var runtime = new StudioJobServiceTests.Runtime();
        var service = new StudioJobService(new(new(box.Root), box.Catalog(), new()), runtime, resolver, training);
        string modelId = new string('1', 32); var request = new GenerationRequest(JobKind.Preview, [prepared.Segments[0].Id], modelId);
        Assert.Equal("training.model.confirmation_required", (await Assert.ThrowsAsync<StudioException>(() => service.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "no-consent", request, Token))).Code);
        Assert.Equal(0, resolver.Calls);
        var job = await service.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "saved", request with { ExperimentalConfirmed = true }, Token);
        Assert.Equal(1, resolver.Calls); var frozen = resolver.Last;
        resolver.Step = 250; runtime.Ready = false;
        Assert.Equal(job, await service.EnqueueAsync(prepared.Metadata.Id, -1, "saved", request with { ExperimentalConfirmed = true }, Token));
        Assert.Equal(1, resolver.Calls);
        await using var db = await new LocalAudiobookStudio.Core.Projects.ProjectService().OpenCurrentAsync(box.ProjectRoot(prepared.Metadata.Id), Token);
        Assert.Equal(frozen, (await db.Jobs.GetAsync(job.Id, Token)).Input.SavedVoice);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<StudioException>(() => service.EnqueueAsync(prepared.Metadata.Id, -1, "saved", request with { SavedModelId = null }, Token))).Code);
    }
    private sealed class Resolver : ITrainingSavedVoiceResolver
    {
        public int Calls, Step = 125; public SavedVoiceBinding? Last;
        public Task<SavedVoiceBinding> ResolveAsync(ProjectDatabase db, string id, CancellationToken token)
        {
            Calls++; var source = db.State.VoiceRecording!;
            Last = new(id, "training/actions/" + new string('2', 32) + "/state/workflow.json", new string('a', 64), source.Id, source.WavRelativePath,
                source.Sha256.ToLowerInvariant(), Step, Step / 125, 125, "target_epoch", "rvc-saved-voice-cpu-v1");
            return Task.FromResult(Last);
        }
    }
}
