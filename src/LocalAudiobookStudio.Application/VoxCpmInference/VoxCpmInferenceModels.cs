using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.VoxCpmInference;

public sealed record VoxCpmInferenceIdentity(VoxCpmTrainingIdentity Training, string ImplementationSha256);
public interface IVoxCpmInferenceRuntime
{
    Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default);
    Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token);
}
public interface IVoxCpmSavedVoiceResolver
{
    Task<ResolvedVoxCpmSavedVoice> ResolveAsync(ProjectDatabase project, string modelId, CancellationToken token);
    Task<ResolvedVoxCpmSavedVoice> ResolveConfiguredAsync(ProjectDatabase project, string modelId, VoxCpmGenerationOptions options, CancellationToken token)
        => Task.FromException<ResolvedVoxCpmSavedVoice>(new WorkerClientException("worker.runtime.not_ready", true));
    Task<ResolvedVoxCpmSavedVoice> ResolveForExecutionAsync(ProjectDatabase project, VoxCpmSavedVoiceBinding binding, CancellationToken token);
}
/// <summary>Caller retains this provenance owner through confirmed native cleanup.</summary>
public sealed class ResolvedVoxCpmSavedVoice(VoxCpmSavedVoiceBinding binding, VoxCpmCheckpointReference checkpoint,
    ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, IDisposable? ownership = null,
    ApprovedTtsCorpusClip? referenceClip = null) : IDisposable
{
    private IDisposable? pins = ownership;
    public VoxCpmSavedVoiceBinding Binding { get; } = binding;
    public VoxCpmCheckpointReference Checkpoint { get; } = checkpoint;
    public ApprovedTtsCorpusBinding Corpus { get; } = corpus;
    public VoxCpmTrainingIdentity Identity { get; } = identity;
    public ApprovedTtsCorpusClip? ReferenceClip { get; } = referenceClip;
    public void Dispose() => Interlocked.Exchange(ref pins, null)?.Dispose();
}
public sealed record VoxCpmInferenceExecution(string ProjectRoot, string OutputRoot, string JobId,
    string SegmentId, string AttemptId, string Text, ResolvedVoxCpmSavedVoice Voice);
public sealed record VoxCpmInferenceCandidate(string Path, string Sha256, long Length, long SampleCount);
public interface IVoxCpmInferenceExecutor
{
    Task<VoxCpmInferenceCandidate> RunAsync(VoxCpmInferenceExecution execution, Func<string, Task> progress, CancellationToken token);
}
public sealed class UnavailableVoxCpmInferenceRuntime : IVoxCpmInferenceRuntime
{
    public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new RuntimeReadiness(false, true, "worker.runtime.not_ready", VoxCpmInferenceProfile.Id)); }
    public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)
        => Task.FromException<VoxCpmInferenceIdentity>(new WorkerClientException("worker.runtime.not_ready", true));
}
public sealed class UnavailableVoxCpmInferenceExecutor : IVoxCpmInferenceExecutor
{
    public Task<VoxCpmInferenceCandidate> RunAsync(VoxCpmInferenceExecution execution, Func<string, Task> progress, CancellationToken token)
        => Task.FromException<VoxCpmInferenceCandidate>(new WorkerClientException("worker.runtime.not_ready", true));
}
public static class VoxCpmInferenceProfile
{
    public const string Id = "voxcpm2-saved-adapter-cpu-v1";
    public const string ReferenceId = "voxcpm2-saved-adapter-reference-cpu-v1";
    public const string ReferencePresetId = "voxcpm2-saved-adapter-reference-v1";
    public const string ControlsId = "voxcpm2-saved-adapter-controls-cpu-v1";
    public const string ControlsPresetId = "voxcpm2-saved-adapter-controls-v1";
    public static string IdentityHash(VoxCpmTrainingIdentity identity)
    {
        string[] fields = ["voxcpm-inference-training-identity-v1", identity.Fingerprint, identity.SourceCommit,
            identity.BaseRevision, identity.SourceLockSha256, identity.ModelLockSha256, identity.CacheSha256,
            identity.CacheLayoutSha256, identity.LoaderSha256, identity.RecipeSha256, identity.AdapterLayoutSha256];
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static RuntimeDescriptor Descriptor(VoxCpmSavedVoiceBinding binding, VersionPins versions)
    {
        bool legacy = binding.ProfileVersion == Id && binding.Reference == null && binding.Options == null;
        bool reference = binding.ProfileVersion == ReferenceId && binding.Reference != null && binding.Options == null;
        var options = binding.Options;
        bool controls = binding.ProfileVersion == ControlsId && options is { } && options.IsValid()
            && (options.UseReference == (binding.Reference != null)) && (options.ReferenceClipId == null || options.ReferenceClipId == binding.Reference?.Id);
        if (!Hash(binding.InferenceImplementationSha256) || !Hash(binding.ManifestSha256) || !(legacy || reference || controls))
            throw new WorkerClientException("worker.runtime.not_ready", true);
        return new(new(controls ? ControlsPresetId : reference ? ReferencePresetId : "voxcpm2-saved-adapter-v1", binding.ProfileVersion,
            256, options?.Timesteps ?? 10, options?.Cfg ?? 2, 1, options?.Seed ?? 42, false), versions with {
            Worker = binding.InferenceImplementationSha256[..40], Model = binding.ManifestSha256[..40] });
    }
}
