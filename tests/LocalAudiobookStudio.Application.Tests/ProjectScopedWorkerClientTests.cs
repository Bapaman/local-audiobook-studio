using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.Application.Tests;

public class ProjectScopedWorkerClientTests
{
    [Fact]
    public async Task Absolute_application_paths_are_relative_on_wire_and_result_restores_owned_absolute_identity()
    {
        using var box = new CatalogSandbox(); var inner = new Client(); await using var scoped = new ProjectScopedWorkerClient(box.Root, inner);
        string prompt = Path.Combine(box.Root, "voice-snapshot", "one.wav"), output = Path.Combine(box.Root, "segments", "source.wav");
        var command = new SynthesizeSegmentCommand("test", prompt, "test", output, "q8-f16-cpu", 70, 10, 2, 1, 42);
        var result = await scoped.SynthesizeAsync(new string('1', 32), command, _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal("voice-snapshot/one.wav", inner.Command!.PromptWav); Assert.Equal("segments/source.wav", inner.Command.FinalPath);
        Assert.Equal(output, result.FinalPath);
    }
    [Fact]
    public async Task Outside_project_path_is_rejected_before_wire_send()
    {
        using var box = new CatalogSandbox(); var inner = new Client(); await using var scoped = new ProjectScopedWorkerClient(box.Root, inner);
        var command = new SynthesizeSegmentCommand("test", Path.Combine(Path.GetDirectoryName(box.Root)!, "outside.wav"), "test", Path.Combine(box.Root, "final.wav"), "q8-f16-cpu", 70, 10, 2, 1, 42);
        Assert.Equal("worker.path", (await Assert.ThrowsAsync<WorkerClientException>(() => scoped.SynthesizeAsync(new string('1', 32), command, _ => { }, TestContext.Current.CancellationToken))).Code);
        Assert.Null(inner.Command);
    }
    private sealed class Client : IWorkerClient
    {
        public SynthesizeSegmentCommand? Command;
        public Task<SynthesisResult> SynthesizeAsync(string id, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
        { Command = command; return Task.FromResult(new SynthesisResult(command.FinalPath, 100, new string('a', 64), 1, 48000, 16, 1, command.ProfileId, new string('b', 40), new string('c', 40))); }
        public Task CancelAsync(string id, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
