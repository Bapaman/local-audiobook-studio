using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.VoxCpmInference;

/// <summary>Resolve known accepted references only; never adopt Current.Head or scan files.</summary>
public sealed class VoxCpmSavedVoiceResolver(IApprovedTtsCorpusResolver corpora,
    IVoxCpmTrainingCheckpointValidator checkpoints, IVoxCpmInferenceRuntime runtime,
    bool useCorpusReference = false) : IVoxCpmSavedVoiceResolver
{
    public Task<ResolvedVoxCpmSavedVoice> ResolveAsync(ProjectDatabase project, string modelId, CancellationToken token)
        => Resolve(project, modelId, null, null, token);
    public Task<ResolvedVoxCpmSavedVoice> ResolveConfiguredAsync(ProjectDatabase project, string modelId, VoxCpmGenerationOptions options, CancellationToken token)
        => options is not null && options.IsValid() ? Resolve(project, modelId, null, options, token)
            : Task.FromException<ResolvedVoxCpmSavedVoice>(new StudioException("job.input.invalid"));
    public Task<ResolvedVoxCpmSavedVoice> ResolveForExecutionAsync(ProjectDatabase project, VoxCpmSavedVoiceBinding binding, CancellationToken token)
        => Resolve(project, binding.ModelId, binding, binding.Options, token);

    private async Task<ResolvedVoxCpmSavedVoice> Resolve(ProjectDatabase project, string modelId,
        VoxCpmSavedVoiceBinding? captured, VoxCpmGenerationOptions? options, CancellationToken token)
    {
        ApprovedTtsCorpusLease? corpus = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!VoxCpmTrainingRecordStore.Id(modelId)) throw new StudioException("job.input.invalid");
            var selected = await StudioVoxCpmTrainingService.Store(project).TransactionAsync(e =>
            {
                var point = e.ModelReferences.SingleOrDefault(x => x.Id == modelId);
                var line = point == null ? null : e.Lineages.SingleOrDefault(x => x.TrainingId == point.TrainingId);
                return ((VoxCpmTrainingEnvelope?)null, (Point: point, Line: line));
            }, token);
            var checkpoint = selected.Point; var lineage = selected.Line;
            if (checkpoint == null || lineage == null) throw new StudioException("job.input.invalid");
            Require(project.State.VoiceRecording == lineage.Corpus.Source);
            corpus = await corpora.ResolveAsync(project, lineage.Corpus.VersionId, lineage.Corpus.ManifestSha256, token);
            Require(VoxCpmTrainingRecordStore.SameCorpus(corpus.Binding, lineage.Corpus));
            await checkpoints.VerifyAsync(project.Root, checkpoint, corpus.Binding, lineage.Identity, token);
            ApprovedTtsCorpusClip? referenceClip = null;
            if (captured == null)
            {
                if (options != null)
                {
                    if (options.UseReference)
                    {
                        referenceClip = options.ReferenceClipId == null ? VoxCpmCorpusReferenceSelector.Select(corpus.Binding.Clips)
                            : corpus.Binding.Clips.SingleOrDefault(c => c.Id == options.ReferenceClipId);
                        if (referenceClip == null || !VoxCpmCorpusReferenceSelector.Eligible(referenceClip)) throw new StudioException("job.input.invalid");
                    }
                }
                else if (useCorpusReference) referenceClip = VoxCpmCorpusReferenceSelector.Select(corpus.Binding.Clips);
            }
            else if (captured.ProfileVersion == VoxCpmInferenceProfile.ReferenceId && options == null
                || captured.ProfileVersion == VoxCpmInferenceProfile.ControlsId && options is { UseReference: true } && options.IsValid())
            {
                Require(captured.Reference != null);
                referenceClip = corpus.Binding.Clips.SingleOrDefault(c => c.Id == captured.Reference!.Id);
                Require(referenceClip != null && VoxCpmCorpusReferenceSelector.Eligible(referenceClip)
                    && VoxCpmCorpusReferenceSelector.Capture(referenceClip) == captured.Reference
                    && (options?.ReferenceClipId == null || options.ReferenceClipId == referenceClip.Id));
            }
            else Require(captured.Reference == null && (captured.ProfileVersion == VoxCpmInferenceProfile.Id && options == null
                || captured.ProfileVersion == VoxCpmInferenceProfile.ControlsId && options is { UseReference: false } && options.IsValid()));
            string implementation;
            if (captured == null)
            {
                var current = await runtime.GetIdentityAsync(token);
                if (current.Training != lineage.Identity || !VoxCpmInferenceProfile.Hash(current.ImplementationSha256))
                    throw new WorkerClientException("worker.runtime.not_ready", true);
                implementation = current.ImplementationSha256;
            }
            else implementation = captured.InferenceImplementationSha256; // Native window re-admits this exact implementation.
            Require(VoxCpmInferenceProfile.Hash(implementation));
            var b = corpus.Binding; var cursor = checkpoint.Cursor;
            var binding = new VoxCpmSavedVoiceBinding(checkpoint.Id, checkpoint.TrainingId, checkpoint.ManifestRelativePath,
                checkpoint.ManifestSha256, b.VersionId, b.ManifestSha256, b.BindingSha256, b.Source.Id,
                b.Source.Sha256.ToLowerInvariant(), VoxCpmInferenceProfile.IdentityHash(lineage.Identity), cursor.GlobalStep,
                cursor.CompletedEpochs, cursor.EpochSize, implementation,
                options != null ? VoxCpmInferenceProfile.ControlsId : referenceClip == null ? VoxCpmInferenceProfile.Id : VoxCpmInferenceProfile.ReferenceId,
                referenceClip == null ? null : VoxCpmCorpusReferenceSelector.Capture(referenceClip), options);
            if (captured != null) Require(captured == binding);
            token.ThrowIfCancellationRequested();
            var result = new ResolvedVoxCpmSavedVoice(binding, checkpoint, b, lineage.Identity, corpus, referenceClip);
            corpus = null; // Transfer real source/corpus pins, not only their JSON metadata.
            return result;
        }
        catch (StudioException e) when (e.Code is not ("job.input.invalid" or "job.input_changed"))
        { throw new StudioException("job.input_changed"); }
        catch (Exception e) when (e is VoxCpmTrainingExecutionException or ProjectException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or OverflowException or NotSupportedException)
        { throw new StudioException("job.input_changed"); }
        finally { corpus?.Dispose(); }
    }
    private static void Require(bool valid) { if (!valid) throw new StudioException("job.input_changed"); }
}
