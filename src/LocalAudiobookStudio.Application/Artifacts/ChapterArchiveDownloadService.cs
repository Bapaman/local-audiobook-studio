using System.Globalization;
using System.IO.Compression;
using System.Text;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Application.Artifacts;

public sealed class ChapterArchiveDownloadService(IStudioProjectService projects, IProjectArtifactReader artifacts)
{
    private sealed record Chapter(int Number, string Title, SegmentDto[] Segments);

    public Task<Stream> OpenAsync(string projectId, long revision, CancellationToken token = default)
        => OpenAsync(projectId, revision, null, token);

    /// <summary>Builds and verifies the archive before transferring ownership of its temporary stream.</summary>
    public async Task<Stream> OpenAsync(string projectId, long revision, string? textDocumentId, CancellationToken token = default)
    {
        var before = await projects.GetAsync(projectId, token);
        if (revision < 1 || before.Revision != revision) throw new StudioException("project.conflict");
        var chapters = OrderedChapters(before, textDocumentId);
        int numberWidth = NumberWidth(before.TextDocuments?.Count ?? 1);

        token.ThrowIfCancellationRequested();
        string path = Path.Combine(Path.GetTempPath(), $"las-chapters-{Guid.NewGuid():N}.zip");
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        bool transferred = false;
        try
        {
            await using (var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create,
                leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancellationToken: token))
            {
                foreach (var chapter in chapters)
                {
                    string prefix = $"{Number(chapter.Number, numberWidth)} - {SafeTitle(chapter.Title)}";
                    await WriteChapterAsync(zip, projectId, chapter.Segments, prefix, token);
                }
            }

            await output.FlushAsync(token);
            var after = await projects.GetAsync(projectId, token);
            if (after.Revision != revision || !before.Segments.SequenceEqual(after.Segments)
                || before.TextDocument?.DisplayName != after.TextDocument?.DisplayName
                || before.DisplayName != after.DisplayName
                || !SameChapters(before.TextDocuments, after.TextDocuments))
                throw new StudioException("project.conflict");

            token.ThrowIfCancellationRequested();
            output.Position = 0;
            transferred = true;
            return output;
        }
        catch (WavInspectionException error)
        {
            throw new StudioException(error.Code.StartsWith("book.", StringComparison.Ordinal) ? error.Code : "artifact.invalid");
        }
        catch (EndOfStreamException) { throw new StudioException("artifact.invalid"); }
        finally
        {
            if (!transferred) await output.DisposeAsync();
        }
    }

    private async Task WriteChapterAsync(ZipArchive zip, string projectId, SegmentDto[] segments, string prefix, CancellationToken token)
    {
        var handles = new List<ArtifactReadHandle>();
        long length = 0;
        int partsWritten = 0, partWidth = NumberWidth(segments.Length);
        try
        {
            foreach (var segment in segments)
            {
                if (handles.Count == PcmWavAssembly.MaximumFragments) await WritePartAsync(split: true);

                var handle = await artifacts.OpenAsync(projectId, segment.OutputArtifactId!, token);
                bool owned = false;
                try
                {
                    long pcmBytes = await PcmWavAssembly.ReadPcmLengthAsync(handle.Content, token);
                    if (pcmBytes > PcmWavAssembly.MaximumPcmBytes) throw new WavInspectionException("book.byte_limit");
                    if (pcmBytes > PcmWavAssembly.MaximumPcmBytes - length) await WritePartAsync(split: true);
                    handles.Add(handle);
                    owned = true;
                    length += pcmBytes;
                }
                finally { if (!owned) await handle.DisposeAsync(); }
            }
            await WritePartAsync(split: partsWritten > 0);
        }
        finally { await BookReadHandle.ReleaseAsync(handles); }

        async Task WritePartAsync(bool split)
        {
            var audio = await PcmWavAssembly.CreateAsync(handles.Select(handle => handle.Content).ToArray(), token);
            string suffix = split ? $" - часть {Number(partsWritten + 1, partWidth)}" : "";
            var entry = zip.CreateEntry($"{prefix}{suffix}.wav", CompressionLevel.NoCompression);
            await using (var content = await entry.OpenAsync(token))
            {
                await audio.CopyToAsync(content, token);
            }
            await BookReadHandle.ReleaseAsync(handles);
            handles.Clear();
            partsWritten++;
            length = 0;
        }
    }

    private static Chapter[] OrderedChapters(ProjectSnapshotDto snapshot, string? textDocumentId)
    {
        var selected = textDocumentId == null ? null : BookDownloadService.ResolveDocument(snapshot, textDocumentId);
        if (snapshot.TextDocuments is not { Count: > 0 } documents)
            return [new(1, snapshot.TextDocument?.DisplayName ?? snapshot.DisplayName, BookDownloadService.OrderAcceptedSegments(snapshot.Segments))];

        if (documents.Select(document => document.Order).Distinct().Count() != documents.Count
            || documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw new StudioException("book.incomplete");

        var ordered = documents.OrderBy(document => document.Order)
            .Select((document, index) => (Document: document, Number: index + 1))
            .Where(entry => selected == null || entry.Document.Id == selected.Id);
        var chapters = ordered.Select(entry => new Chapter(entry.Number, entry.Document.Document.DisplayName,
            BookDownloadService.OrderAcceptedSegments(BookDownloadService.SelectSegments(snapshot, entry.Document)))).ToArray();
        if (selected == null)
        {
            var allSegments = chapters.SelectMany(chapter => chapter.Segments).ToArray();
            if (allSegments.Length != snapshot.Segments.Count) throw new StudioException("book.incomplete");
            BookDownloadService.OrderAcceptedSegments(allSegments);
        }
        return chapters;
    }

    private static int NumberWidth(int count) => Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
    private static string Number(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static bool SameChapters(IReadOnlyList<ProjectTextDocumentDto>? expected, IReadOnlyList<ProjectTextDocumentDto>? actual)
        => expected == null ? actual == null :
            actual != null && actual.Count == expected.Count
            && expected.OrderBy(document => document.Order).Zip(actual.OrderBy(document => document.Order)).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Order == pair.Second.Order
                && pair.First.Document.DisplayName == pair.Second.Document.DisplayName
                && pair.First.SegmentIds.SequenceEqual(pair.Second.SegmentIds, StringComparer.Ordinal));

    private static string SafeTitle(string title)
    {
        const string invalidCharacters = "<>:\"/\\|?*";
        const int maxTitleBytes = 180;
        var safe = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in title.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                || rune.IsAscii && invalidCharacters.Contains((char)rune.Value))
            {
                if (bytes >= maxTitleBytes) break;
                safe.Append('_');
                bytes++;
            }
            else
            {
                if (bytes + rune.Utf8SequenceLength > maxTitleBytes) break;
                safe.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
        }

        string result = safe.ToString().Trim().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "Глава" : result;
    }
}
