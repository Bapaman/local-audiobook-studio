using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class ChapterArchiveEndpointTests
{
    private const string Path = "/api/projects/book/chapters.zip?revision=1";
    private const string FirstId = "11111111111111111111111111111111";
    private const string SecondId = "22222222222222222222222222222222";

    [Fact]
    public async Task Download_contains_ordered_named_WAVs_when_only_the_combined_book_exceeds_the_fragment_limit()
    {
        var reader = new Reader(startOfSecondChapter: 131);
        await using var host = await StartAsync(new Projects(Snapshot(firstCount: 130, secondCount: 129)), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("chapters.zip", response.Content.Headers.ContentDisposition.FileNameStar);
        using var download = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        Assert.Equal(download.Length, response.Content.Headers.ContentLength);
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);

        Assert.Collection(zip.Entries,
            entry => Assert.Equal("01 - Начало.wav", entry.FullName),
            entry => Assert.Equal("02 - Часть _ 2_ финал_.wav", entry.FullName));
        Assert.Equal(Enumerable.Range(1, 130).Select(number => (short)number), await ReadSamplesAsync(zip.Entries[0]));
        Assert.Equal(Enumerable.Range(131, 129).Select(number => (short)number), await ReadSamplesAsync(zip.Entries[1]));
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
    }

    [Fact]
    public async Task Archive_requires_authentication_and_the_requested_revision()
    {
        var reader = new Reader();
        await using var host = await StartAsync(new Projects(Snapshot() with { Revision = 2 }), reader);

        using var unauthorized = await host.Client.GetAsync(Path, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var stale = await host.SendAsync(HttpMethod.Get, Path);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("project.conflict", (await ApiTestHost.JsonAsync(stale)).GetProperty("code").GetString());
        Assert.Empty(reader.Streams);
    }

    [Fact]
    public async Task Archive_rejects_incomplete_chapters_before_downloading()
    {
        var reader = new Reader();
        await using var host = await StartAsync(new Projects(Snapshot(complete: false)), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("book.incomplete", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(reader.Streams);
    }

    [Fact]
    public async Task Long_chapter_is_split_in_order_and_each_part_releases_its_sources()
    {
        var reader = new Reader(startOfSecondChapter: 259, nextPartStart: 257);
        await using var host = await StartAsync(new Projects(Snapshot(firstCount: 258)), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var download = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);

        Assert.Collection(zip.Entries,
            entry => Assert.Equal("01 - Начало - часть 001.wav", entry.FullName),
            entry => Assert.Equal("01 - Начало - часть 002.wav", entry.FullName),
            entry => Assert.Equal("02 - Часть _ 2_ финал_.wav", entry.FullName));
        Assert.Equal(Enumerable.Range(1, 256).Select(number => (short)number), await ReadSamplesAsync(zip.Entries[0]));
        Assert.Equal(new short[] { 257, 258 }, await ReadSamplesAsync(zip.Entries[1]));
        Assert.Equal(new short[] { 259 }, await ReadSamplesAsync(zip.Entries[2]));
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
        using var wholeWav = await host.SendAsync(HttpMethod.Get, "/api/projects/book/book.wav?revision=1");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, wholeWav.StatusCode);
    }

    [Fact]
    public async Task Selected_chapter_exports_while_other_chapters_are_unfinished()
    {
        var reader = new Reader(startOfSecondChapter: 258, nextPartStart: 257);
        await using var host = await StartAsync(new Projects(Snapshot(firstCount: 257, complete: false)), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path + "&textDocumentId=" + FirstId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var download = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);
        Assert.Equal(new[] { "01 - Начало - часть 001.wav", "01 - Начало - часть 002.wav" }, zip.Entries.Select(entry => entry.FullName));
        Assert.Equal(new short[] { 257 }, await ReadSamplesAsync(zip.Entries[1]));
        Assert.Equal(257, reader.Streams.Count);
        using var missing = await host.SendAsync(HttpMethod.Get, Path + "&textDocumentId=33333333333333333333333333333333");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unfinished = await host.SendAsync(HttpMethod.Get, Path + "&textDocumentId=" + SecondId);
        Assert.Equal(HttpStatusCode.Conflict, unfinished.StatusCode);
        Assert.Equal(257, reader.Streams.Count);
    }

    [Fact]
    public async Task A_project_with_one_imported_text_exports_as_ordered_parts()
    {
        var snapshot = Snapshot(firstCount: 257, secondCount: 0);
        var single = snapshot with { TextDocument = snapshot.TextDocuments![1].Document, TextDocuments = null };
        var reader = new Reader(startOfSecondChapter: 258, nextPartStart: 257);
        await using var host = await StartAsync(new Projects(single), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var download = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var zip = new ZipArchive(download, ZipArchiveMode.Read);
        Assert.Equal(new[] { "01 - Начало - часть 001.wav", "01 - Начало - часть 002.wav" }, zip.Entries.Select(entry => entry.FullName));
        Assert.Equal(Enumerable.Range(1, 256).Select(number => (short)number), await ReadSamplesAsync(zip.Entries[0]));
        Assert.Equal(new short[] { 257 }, await ReadSamplesAsync(zip.Entries[1]));
    }

    [Fact]
    public async Task Failure_in_a_later_chapter_returns_the_error_before_a_download_starts()
    {
        var reader = new Reader(failingArtifact: 3);
        await using var host = await StartAsync(new Projects(Snapshot()), reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("artifact.missing", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(2, reader.Streams.Count);
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
    }

    [Fact]
    public async Task Revision_change_while_copying_the_last_WAV_rejects_the_completed_archive()
    {
        var projects = new Projects(Snapshot());
        var reader = new Reader(copyingLastArtifact: () => projects.Revision = 2);
        await using var host = await StartAsync(projects, reader);
        using var response = await host.SendAsync(HttpMethod.Get, Path);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("project.conflict", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(3, reader.Streams.Count);
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
    }

    private static Task<ApiTestHost> StartAsync(Projects projects, Reader reader)
        => ApiTestHost.StartAsync(configureServices: services =>
        {
            services.AddSingleton<IStudioProjectService>(projects);
            services.AddSingleton<IProjectArtifactReader>(reader);
            services.RemoveAll<IHostedService>();
        });

    private static ProjectSnapshotDto Snapshot(int firstCount = 2, int secondCount = 1, bool complete = true)
    {
        var segments = Enumerable.Range(1, firstCount + secondCount)
            .Select(number =>
            {
                string id = number.ToString(CultureInfo.InvariantCulture);
                bool ready = complete || number <= firstCount;
                return new SegmentDto(id, number - 1, "text", "text", "text", "hash", ready ? "done" : "draft", ready ? id : null);
            }).ToArray();
        return new("book", "Synthetic", 1, null, null, null, segments.Reverse().ToArray(),
            TextDocuments:
            [
                new(SecondId, 1, new("Pasted", "Часть / 2: финал?. ", "utf-8", "hash", 4, "text", "text"),
                    segments.Skip(firstCount).Select(segment => segment.Id).ToArray()),
                new(FirstId, 0, new("Pasted", "Начало", "utf-8", "hash", 4, "text", "text"),
                    segments.Take(firstCount).Select(segment => segment.Id).ToArray())
            ]);
    }

    private static async Task<short[]> ReadSamplesAsync(ZipArchiveEntry entry)
    {
        await using var input = await entry.OpenAsync(ApiTestHost.Token);
        var header = new byte[44];
        await input.ReadExactlyAsync(header, ApiTestHost.Token);
        Assert.True(header.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.True(header.AsSpan(8, 4).SequenceEqual("WAVE"u8));
        Assert.Equal(entry.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(entry.Length - 44, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
        var data = new byte[entry.Length - 44];
        await input.ReadExactlyAsync(data, ApiTestHost.Token);
        return Enumerable.Range(0, data.Length / 2)
            .Select(index => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(index * 2))).ToArray();
    }

    private sealed class Projects(ProjectSnapshotDto snapshot) : IStudioProjectService
    {
        public long Revision { get; set; } = snapshot.Revision;
        public Task<ProjectSnapshotDto> GetAsync(string id, CancellationToken token = default)
            => Task.FromResult(snapshot with { Revision = Revision });
        public Task<ProjectSnapshotDto> OpenAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<CreateProjectResult> CreateAsync(CreateProjectCommand command, string key, CancellationToken token = default)
            => throw new NotSupportedException();
    }

    private sealed class Reader(int startOfSecondChapter = 3, int? failingArtifact = null, Action? copyingLastArtifact = null, int? nextPartStart = null)
        : IProjectArtifactReader
    {
        public List<MemoryStream> Streams { get; } = [];

        public Task<ArtifactReadHandle> OpenAsync(string projectId, string artifactId, CancellationToken token = default)
        {
            short number = short.Parse(artifactId, CultureInfo.InvariantCulture);
            if (number == startOfSecondChapter || number == nextPartStart)
                Assert.All(Streams, stream => Assert.False(stream.CanRead));
            if (number == failingArtifact) throw new StudioException("artifact.missing");
            byte[] wav = ApiTestHost.Wav(1);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44), number);
            var stream = new SampleStream(wav, number == startOfSecondChapter ? copyingLastArtifact : null);
            Streams.Add(stream);
            return Task.FromResult(new ArtifactReadHandle(stream, wav.Length, "audio/wav", "audio.wav"));
        }
    }

    private sealed class SampleStream(byte[] wav, Action? onSampleRead) : MemoryStream(wav)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 44) onSampleRead?.Invoke();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
