using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;
public sealed class VoxCpmSavedVoiceEnqueueTests
{
    [Fact]
    public async Task Consent_provider_conflict_and_replay_are_checked_before_runtime_resolution()
    {
        using var box=new CatalogSandbox();var prepared=await StudioJobServiceTests.Prepare(box);var resolver=new VoxProductResolver();
        var service=new StudioJobService(new(new(box.Root),box.Catalog(),new()),new NeverLegacyDescriptor(),voxSavedVoices:resolver);
        var request=new GenerationRequest(JobKind.Preview,[prepared.Segments[0].Id],SavedVoxCpmModelId:new('4',32));var token=TestContext.Current.CancellationToken;
        var consent=await Assert.ThrowsAsync<StudioException>(()=>service.EnqueueAsync(prepared.Metadata.Id,prepared.Revision,"consent",request,token));
        Assert.Equal("training.model.confirmation_required",consent.Code);Assert.Equal(0,resolver.Calls);
        var conflict=await Assert.ThrowsAsync<StudioException>(()=>service.EnqueueAsync(prepared.Metadata.Id,prepared.Revision,"both",request with{SavedModelId=new('5',32),ExperimentalConfirmed=true},token));
        Assert.Equal("job.input.invalid",conflict.Code);Assert.Equal(0,resolver.Calls);
        request=request with{ExperimentalConfirmed=true};var job=await service.EnqueueAsync(prepared.Metadata.Id,prepared.Revision,"vox",request,token);
        resolver.Reject=true;Assert.Equal(job,await service.EnqueueAsync(prepared.Metadata.Id,-1,"vox",request,token));Assert.Equal(1,resolver.Calls);Assert.Equal(1,resolver.Disposed);
        Assert.Equal("job.idempotency.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.EnqueueAsync(prepared.Metadata.Id,-1,"vox",request with{SavedVoxCpmModelId=null},token))).Code);
    }
}
