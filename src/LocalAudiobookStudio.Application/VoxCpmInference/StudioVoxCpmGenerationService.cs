using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.VoxCpmInference;

public sealed record VoxCpmReferenceClipDto(string Id, int TrainingOrdinal, double Seconds, string Text);
public sealed record VoxCpmReferenceCatalogDto(string ModelId, string CorpusVersionId, string? DefaultClipId,
    IReadOnlyList<VoxCpmReferenceClipDto> Clips);
public interface IStudioVoxCpmGenerationService
{
    Task<VoxCpmReferenceCatalogDto> GetReferencesAsync(string projectId, string modelId, CancellationToken token = default);
    Task<TtsCorpusAudioHandle> OpenAudioAsync(string projectId, string modelId, string clipId, CancellationToken token = default);
}

/// <summary>Read approved model lineage without admitting the inference runtime or adopting editable draft clips.</summary>
public sealed class StudioVoxCpmGenerationService(ProjectJobAccess access, IApprovedTtsCorpusResolver corpora) : IStudioVoxCpmGenerationService
{
    public Task<VoxCpmReferenceCatalogDto> GetReferencesAsync(string projectId, string modelId, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            using var corpus = await ResolveCorpusAsync(db, modelId, ct);
            var eligible = corpus.Binding.Clips.Where(VoxCpmCorpusReferenceSelector.Eligible).OrderBy(x => x.TrainingOrdinal).ToArray();
            string? selected = eligible.Length == 0 ? null : VoxCpmCorpusReferenceSelector.Select(eligible).Id;
            return new VoxCpmReferenceCatalogDto(modelId, corpus.Binding.VersionId, selected,
                Array.AsReadOnly(eligible.Select(x => new VoxCpmReferenceClipDto(x.Id, x.TrainingOrdinal,
                    (x.EndSample - x.StartSample) / 16000.0, x.CorrectedText)).ToArray()));
        }, token);

    public async Task<TtsCorpusAudioHandle> OpenAudioAsync(string projectId, string modelId, string clipId, CancellationToken token = default)
    {
        if (!CatalogPaths.IsId(clipId)) throw new StudioException("job.input.invalid");
        var pins = new CatalogPaths.Pins(); FileStream? content = null; bool transferred = false;
        try
        {
            return await WithProject(projectId, async (db, ct) =>
            {
                var corpus = await ResolveCorpusAsync(db, modelId, ct); pins.Add(corpus);
                var clip = corpus.Binding.Clips.SingleOrDefault(x => x.Id == clipId && VoxCpmCorpusReferenceSelector.Eligible(x))
                    ?? throw new StudioException("job.input.invalid");
                string path = db.ResolveOwnedPath(clip.AudioRelativePath); CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins);
                content = TtsCorpusSource.OpenRead(path);
                await TtsCorpusSource.ValidateAudioAsync(content, new(clip.Id, clip.SourceIndex, clip.StartSample, clip.EndSample,
                    clip.AudioRelativePath, clip.AudioSha256), ct);
                var handle = new TtsCorpusAudioHandle(content, pins); transferred = true; return handle;
            }, token);
        }
        finally
        {
            if (!transferred) { try { if (content != null) await content.DisposeAsync(); } finally { pins.Dispose(); } }
        }
    }

    private async Task<ApprovedTtsCorpusLease> ResolveCorpusAsync(ProjectDatabase db, string modelId, CancellationToken token)
    {
        if (!VoxCpmTrainingRecordStore.Id(modelId)) throw new StudioException("job.input.invalid");
        var selected = await StudioVoxCpmTrainingService.Store(db).TransactionAsync(e =>
        {
            var point = e.ModelReferences.SingleOrDefault(x => x.Id == modelId);
            var line = point == null ? null : e.Lineages.SingleOrDefault(x => x.TrainingId == point.TrainingId);
            return ((VoxCpmTrainingEnvelope?)null, line);
        }, token);
        if (selected == null) throw new StudioException("job.input.invalid");
        if (db.State.VoiceRecording != selected.Corpus.Source) throw new StudioException("job.input_changed");
        var corpus = await corpora.ResolveAsync(db, selected.Corpus.VersionId, selected.Corpus.ManifestSha256, token);
        if (VoxCpmTrainingRecordStore.SameCorpus(corpus.Binding, selected.Corpus)) return corpus;
        corpus.Dispose(); throw new StudioException("job.input_changed");
    }

    private async Task<T> WithProject<T>(string projectId, Func<ProjectDatabase, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try
        {
            await using var session = await access.OpenCurrentAsync(projectId, token);
            await using var lease = await session.Project.AcquireArtifactLeaseAsync(cancellationToken: token);
            await lease.RefreshAsync(token);
            return await action(session.Project, token);
        }
        catch (StudioException e) when (e.Code.StartsWith("tts_corpus.", StringComparison.Ordinal)
            || e.Code.StartsWith("voxcpm_training.", StringComparison.Ordinal)) { throw new StudioException("job.input_changed"); }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or OverflowException or NotSupportedException) { throw new StudioException("job.input_changed"); }
    }
}
