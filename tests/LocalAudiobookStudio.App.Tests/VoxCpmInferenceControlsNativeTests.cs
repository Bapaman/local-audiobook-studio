using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmInferenceControlsNativeTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Tempo_cache_is_owned_without_reference_and_retained_until_confirmed_cleanup(bool unknownCleanup)
    {
        using var f = new VoxInferenceFixture(); f.UseOptions(new(UseReference:false,TempoPercent:90));
        var launcher = new VoxInferenceLauncher(f) { Fault="success" }; launcher.Child.StopFails=unknownCleanup;
        var runtime = new VoxCpmInferenceRuntime(new(f.Training.Options,new VoxNativeInventory(f.Training)),f.Scripts,
            Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json"));
        var executor = new VoxCpmInferenceNativeExecutor(runtime,launcher,new VoxNativeMemory());
        string cache = Path.Combine(f.Execution.OutputRoot,"decoder-cache");
        try
        {
            if (unknownCleanup)
            {
                var error=await Assert.ThrowsAsync<WorkerClientException>(()=>executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken));
                Assert.False(error.CleanupConfirmed); Assert.Throws<IOException>(()=>Directory.Move(cache,cache+"-moved"));
            }
            else Assert.Equal(111,(await executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken)).SampleCount);
            Assert.Equal(VoxCpmTrainingFilePins.Extended(cache),launcher.StartInfo!.Environment["NUMBA_CACHE_DIR"]);
            Assert.Equal("0",launcher.StartInfo.Environment["LIBROSA_CACHE_LEVEL"]);
            Assert.False(launcher.StartInfo.Environment.ContainsKey("LIBROSA_CACHE_DIR"));
        }
        finally { launcher.Child.StopFails=false; await executor.DisposeAsync(); }
        Directory.Move(cache,cache+"-moved"); Directory.Move(cache+"-moved",cache);
    }
}
