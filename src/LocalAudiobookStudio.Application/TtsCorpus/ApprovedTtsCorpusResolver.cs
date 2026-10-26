using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

/// <summary>Resolves only committed references, never current draft clips or directory-discovered artifacts.</summary>
public sealed class ApprovedTtsCorpusResolver : IApprovedTtsCorpusResolver
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<ApprovedTtsCorpusLease> ResolveAsync(ProjectDatabase project, string versionId,
        string? expectedManifestSha256, CancellationToken token)
    {
        var pins = new CatalogPaths.Pins(); bool transferred = false;
        try
        {
            token.ThrowIfCancellationRequested();
            Require(TtsCorpusRecordStore.Id(versionId));
            if (expectedManifestSha256 != null) Require(TtsCorpusRecordStore.HashValid(expectedManifestSha256));
            var recorded = await StudioTtsCorpusService.Store(project).TransactionAsync(e =>
            {
                var reference = e.Current?.VersionReferences.SingleOrDefault(x => x.Id == versionId);
                var summary = e.Current?.Snapshot.Versions.SingleOrDefault(x => x.Id == versionId);
                return ((TtsCorpusEnvelope?)null, (Reference: reference, Summary: summary));
            }, token);
            var reference = recorded.Reference; var summary = recorded.Summary;
            Require(reference != null && summary != null);
            if (expectedManifestSha256 != null) Require(TtsCorpusRecordStore.SameHash(reference!.Sha256, expectedManifestSha256));
            var manifest = OpenPinned(project, reference!.ManifestRelativePath, reference.ActionId, pins);
            Require(manifest.Length is > 0 and <= TtsCorpusRecordStore.MaximumBytes);
            byte[] bytes = new byte[checked((int)manifest.Length)]; await manifest.ReadExactlyAsync(bytes, token);
            string manifestSha = Sha(bytes); Require(TtsCorpusRecordStore.SameHash(manifestSha, reference.Sha256));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement; UniqueFields(root);
            Fields(root, "Format", "ProjectId", "CorpusId", "VersionId", "Source", "SourceProjectRevision", "RuntimeFingerprint", "PlanSha256", "Clips");
            Require(Integer(root, "Format") == 1 && Text(root, "ProjectId") == project.State.Metadata.Id
                && Text(root, "VersionId") == versionId && TtsCorpusRecordStore.Id(Text(root, "CorpusId")));
            string corpusId = Text(root, "CorpusId"), corpusRuntime = Hash(root, "RuntimeFingerprint"), planSha = Hash(root, "PlanSha256");
            long sourceRevision = Integer(root, "SourceProjectRevision"); Require(sourceRevision >= 1);
            var sourceJson = root.GetProperty("Source");
            Fields(sourceJson, "Id", "WavRelativePath", "Sha256", "ByteLength", "SampleCount", "HasClippingWarning", "RightsConfirmed", "ImportedUtc");
            Require(Integer(sourceJson, "ByteLength") > 0 && Integer(sourceJson, "SampleCount") > 0);
            var source = sourceJson.Deserialize<VoiceRecordingSnapshot>(Json); Require(source != null);
            pins.Add(await TtsCorpusSource.VerifyAsync(project, source!, token));
            long totalSamples = checked((source!.SampleCount + 2) / 3);
            var values = root.GetProperty("Clips"); Require(values.ValueKind == JsonValueKind.Array && values.GetArrayLength() is >= 1 and <= 512);
            var clips = new List<ApprovedTtsCorpusClip>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            int previousIndex = -1; long previousEnd = 0, retainedSamples = 0;
            foreach (var value in values.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                Fields(value, "Id", "Index", "StartSample", "EndSample", "Text", "AudioSha256", "AudioRelativePath", "OriginalTextSha256");
                string id = Text(value, "Id"), text = Text(value, "Text"), audioPath = Text(value, "AudioRelativePath");
                long index = Integer(value, "Index"), start = Integer(value, "StartSample"), end = Integer(value, "EndSample");
                Require(TtsCorpusRecordStore.Id(id) && ids.Add(id) && index > previousIndex && index < 512
                    && start >= previousEnd && end > start && end <= totalSamples && end - start <= 256000);
                Require(text.Length is > 0 and <= 4000 && !text.Any(char.IsControl) && TtsCorpusRecordStore.Normalize(text) == text);
                string audioSha = Hash(value, "AudioSha256"), originalTextSha = Hash(value, "OriginalTextSha256");
                var audio = OpenPinned(project, audioPath, reference.ActionId, pins);
                await TtsCorpusSource.ValidateAudioAsync(audio, new(id, (int)index, start, end, audioPath, audioSha), token);
                clips.Add(new(clips.Count, (int)index, id, start, end, text, Sha(Encoding.UTF8.GetBytes(text)), audioPath, audioSha, originalTextSha));
                retainedSamples = checked(retainedSamples + end - start); previousIndex = (int)index; previousEnd = end;
            }
            double duration = retainedSamples / 16000.0;
            // Approval sums per-clip doubles; do not reject its harmless summation roundoff.
            Require(summary!.ClipCount == clips.Count && Math.Abs(summary.RetainedDurationSeconds - duration) <= 1e-8);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData("voxcpm2-approved-binding-v1\n"u8); digest.AppendData(bytes);
            var binding = new ApprovedTtsCorpusBinding(project.State.Metadata.Id, corpusId, versionId, reference.ActionId,
                reference.ManifestRelativePath, manifestSha, Convert.ToHexStringLower(digest.GetHashAndReset()), source,
                sourceRevision, corpusRuntime, planSha, duration, new ReadOnlyCollection<ApprovedTtsCorpusClip>(clips));
            token.ThrowIfCancellationRequested();
            var result = new ApprovedTtsCorpusLease(binding, pins); transferred = true; return result;
        }
        catch (Exception e) when (e is StudioException or ProjectException or JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { throw new StudioException("tts_corpus.approved.invalid"); }
        finally { if (!transferred) pins.Dispose(); }
    }

    private static FileStream OpenPinned(ProjectDatabase project, string relative, string action, CatalogPaths.Pins pins)
    {
        Require(!relative.Contains('\\') && relative.StartsWith($"tts-corpus/actions/{action}/", StringComparison.Ordinal));
        string path = project.ResolveOwnedPath(relative);
        CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins);
        var file = TtsCorpusSource.OpenRead(path); pins.Add(file); return file;
    }
    private static string Text(JsonElement value, string name)
    {
        var field = value.GetProperty(name); Require(field.ValueKind == JsonValueKind.String); return field.GetString()!;
    }
    private static long Integer(JsonElement value, string name)
    {
        var field = value.GetProperty(name); Require(field.ValueKind == JsonValueKind.Number);
        string raw = field.GetRawText();
        Require(raw.All(char.IsAsciiDigit) && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return long.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
    }
    private static string Hash(JsonElement value, string name)
    { string text = Text(value, name); Require(TtsCorpusRecordStore.HashValid(text)); return text.ToLowerInvariant(); }
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var fields = value.EnumerateObject().Select(x => x.Name).ToArray();
        Require(fields.Length == names.Length && fields.All(x => names.Contains(x, StringComparer.Ordinal)));
    }
    private static void UniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { Require(names.Add(field.Name)); UniqueFields(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueFields(item);
    }
    private static void Require(bool condition)
    { if (!condition) throw new StudioException("tts_corpus.approved.invalid"); }
}
