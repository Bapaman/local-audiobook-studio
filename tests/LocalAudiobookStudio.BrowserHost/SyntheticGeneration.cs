using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;

// Explicit BrowserHost-only boundary double. Production App never references this project.
internal static class SyntheticGeneration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IRuntimeReadiness, Runtime>();
        services.AddSingleton<IRuntimeDescriptor, Runtime>();
        services.AddSingleton<IWorkerClientFactory, Workers>();
    }
    private sealed class Runtime : IRuntimeReadiness, IRuntimeDescriptor
    {
        public Task<RuntimeReadiness> GetAsync(CancellationToken token)
            => Task.FromResult(new RuntimeReadiness(true, true, null, "q8-f16-cpu"));
        Task<RuntimeDescriptor> IRuntimeDescriptor.GetAsync(CancellationToken token)
            => Task.FromResult(new RuntimeDescriptor(new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false),
                new("", new string('1', 40), new string('2', 40), "", "", "")));
    }
    private sealed class Workers(IStudioJobService jobs) : IWorkerClientFactory
    {
        public Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token)
            => Task.FromResult<IWorkerClient>(new Client(jobs, Path.GetFileName(projectRoot)));
    }
    private sealed class Client(IStudioJobService jobs, string projectId) : IWorkerClient
    {
        private readonly CancellationTokenSource canceled = new();
        public async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, canceled.Token);
            progress(new("generating"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                // Hold the first unit of a multi-fragment job until durable Pause/Cancel.
                // This keeps tab reopening independent of timing. Single-fragment
                // and resumed jobs complete normally.
                while (true)
                {
                    var current = (await jobs.ListAsync(projectId, lifetime.Token)).FirstOrDefault(x =>
                        x.State is JobState.Preparing or JobState.Running or JobState.PauseRequested or JobState.CancelRequested);
                    if (current is not { Kind: JobKind.Generation, TotalUnits: > 1, CompletedUnits: 0, RequestedState: JobRequestedState.Run }) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(50), lifetime.Token);
                }
            }
            catch (OperationCanceledException) { throw new WorkerClientException("worker.canceled", true); }
            byte[] bytes = Wave(command.Text);
            await File.WriteAllBytesAsync(command.FinalPath + ".part", bytes, CancellationToken.None);
            File.Move(command.FinalPath + ".part", command.FinalPath);
            return new(command.FinalPath, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, 48000, 16, 1,
                command.ProfileId, new string('1', 40), new string('2', 40));
        }
        public Task CancelAsync(string targetRequestId, CancellationToken token) { canceled.Cancel(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { canceled.Dispose(); return ValueTask.CompletedTask; }
        private static byte[] Wave(string text)
        {
            byte[] bytes = new byte[96044];
            using var stream = new MemoryStream(bytes); using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8); writer.Write(bytes.Length - 8); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(96000);
            int frequency = 220 + text.Sum(character => (int)character) % 120;
            for (int i = 0; i < 48000; i++) writer.Write((short)(1500 * Math.Sin(2 * Math.PI * frequency * i / 48000)));
            return bytes;
        }
    }
}
