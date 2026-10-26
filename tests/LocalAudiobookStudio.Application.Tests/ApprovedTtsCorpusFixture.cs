using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

/// <summary>Synthetic, genuinely approved corpus; never opens real speaker data.</summary>
internal sealed class ApprovedTtsCorpusFixture : IDisposable
{
    internal CatalogSandbox Box { get; } = new();
    internal string ProjectId { get; private set; } = "";
    internal string CorpusId { get; private set; } = "";
    internal string ActionId { get; private set; } = "";
    internal string VersionId { get; private set; } = "";
    internal string ManifestRelativePath { get; private set; } = "";
    internal string ManifestSha256 { get; private set; } = "";
    internal string SourceRelativePath { get; private set; } = "";
    internal string[] AudioPaths { get; private set; } = [];
    internal string[] ClipIds { get; private set; } = [];
    internal long ProjectRevision { get; private set; }
    internal string Root => Box.ProjectRoot(ProjectId);
    internal string ManifestPath => Path.Combine(Root, ManifestRelativePath);
    internal StudioTtsCorpusService Service => new(new ProjectJobAccess(new(Box.Root), Box.Catalog(), new ProjectService()), new TtsCorpusServiceTests.Runtime());
    internal Task<ProjectDatabase> OpenAsync(CancellationToken token) => new ProjectService().OpenCurrentAsync(Root, token);

    internal async Task SeedAsync(CancellationToken token, int[]? clipSamples = null, int[]? excludedIndices = null)
    {
        clipSamples ??= [80000, 80000, 80000];
        excludedIndices ??= [1];
        var project = (await Box.Service().CreateAsync(new("Synthetic approved corpus"), "project", token)).Snapshot;
        ProjectId = project.Id;
        await using (var db = await new ProjectService().OpenAsync(Root, token, retainSidecars: true))
        {
            await new VoiceRecordingService(new(48000)).ImportAsync(db, new("source.wav", true),
                new MemoryStream(TtsCorpusServiceTests.Wav(48000, clipSamples.Sum() * 3)), token);
            ProjectRevision = db.State.Revision; SourceRelativePath = db.State.VoiceRecording!.WavRelativePath;
        }
        var queued = await Service.StartAsync(ProjectId, ProjectRevision, "start", true, token);
        await using (var db = await OpenAsync(token))
        {
            var draft = await StudioTtsCorpusService.Store(db).TransactionAsync(e =>
            {
                var r = e.Current!; ActionId = r.ActionId; CorpusId = r.Snapshot.Id;
                // Audio predates the execution containing approval, as in the real corpus.
                string audioRoot = $"tts-corpus/actions/{r.ActionId}/executions/{Guid.NewGuid():N}";
                Directory.CreateDirectory(db.ResolveOwnedPath(audioRoot));
                var plans = new List<TtsCorpusPlannedClip>(); var accepted = new List<TtsCorpusAcceptedClip>();
                var clips = new List<TtsCorpusClipDto>(); long cursor = 0;
                for (int i = 0; i < clipSamples.Length; i++)
                {
                    string id = Guid.NewGuid().ToString("N"), wavPath = $"{audioRoot}/{id}.wav", textPath = $"{audioRoot}/{id}.json";
                    byte[] wav = TtsCorpusServiceTests.Wav(16000, clipSamples[i]);
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { Text = "черновик " + i });
                    File.WriteAllBytes(db.ResolveOwnedPath(wavPath), wav); File.WriteAllBytes(db.ResolveOwnedPath(textPath), payload);
                    var plan = new TtsCorpusPlannedClip(id, i, cursor, cursor + clipSamples[i], wavPath, Sha(wav));
                    plans.Add(plan); accepted.Add(new(plan, textPath, Sha(payload), "черновик " + i, [], []));
                    clips.Add(new(id, i, cursor, cursor + clipSamples[i], i == 0 ? "исправлено" : "вторая фраза", excludedIndices.Contains(i), true, [], []));
                    cursor += clipSamples[i];
                }
                string planPath = audioRoot + "/plan.json"; byte[] planBytes = JsonSerializer.SerializeToUtf8Bytes(plans);
                File.WriteAllBytes(db.ResolveOwnedPath(planPath), planBytes);
                var snapshot = queued with { Revision = queued.Revision + 1, State = "completed", Phase = "completed",
                    TotalClipCount = clips.Count, CompletedClipCount = clips.Count, Clips = clips };
                var record = r with { Snapshot = snapshot, Plan = new(planPath, Sha(planBytes), r.Recording.Sha256, r.RuntimeFingerprint, cursor, plans), Accepted = accepted };
                AudioPaths = plans.Select(x => x.AudioRelativePath).ToArray(); ClipIds = plans.Select(x => x.Id).ToArray();
                return (e with { Current = record }, snapshot);
            }, token);
            var approved = await Service.ApproveAsync(ProjectId, CorpusId, draft.Revision, "approve", true, token);
            VersionId = approved.Versions[0].Id; ManifestSha256 = approved.Versions[0].Sha256;
            ManifestRelativePath = await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null,
                e.Current!.VersionReferences.Single(x => x.Id == VersionId).ManifestRelativePath), token);
        }
    }

    internal async Task ReplaceDraftAsync(CancellationToken token)
        => await Service.StartAsync(ProjectId, ProjectRevision, "replacement", true, token);

    internal async Task RewriteManifestAsync(Func<string, string> rewrite, CancellationToken token)
    {
        string changed = rewrite(await File.ReadAllTextAsync(ManifestPath, token));
        await File.WriteAllTextAsync(ManifestPath, changed, new UTF8Encoding(false), token);
        ManifestSha256 = Sha(await File.ReadAllBytesAsync(ManifestPath, token));
        await using var db = await OpenAsync(token);
        await StudioTtsCorpusService.Store(db).TransactionAsync(e =>
        {
            var r = e.Current!;
            return (e with { Current = r with {
                Snapshot = r.Snapshot with { Versions = r.Snapshot.Versions.Select(v => v.Id == VersionId ? v with { Sha256 = ManifestSha256 } : v).ToArray() },
                VersionReferences = r.VersionReferences.Select(v => v.Id == VersionId ? v with { Sha256 = ManifestSha256 } : v).ToArray() } }, true);
        }, token);
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() => Box.Dispose();
}
