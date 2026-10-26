using System.Buffers.Binary;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class BookDownloadServiceTests
{
    private static ProjectSnapshotDto Snapshot(long revision = 1, bool missing = false) => new("project", "Book", revision, null, null, null,
        [new("second", 2, "two", "two", "two", "hash", "Ready", "b"), new("first", 1, "one", "one", "one", "hash", "Ready", missing ? null : "a")]);
    [Fact]
    public async Task Download_orders_current_outputs_and_releases_every_pin_after_copy()
    {
        var reader = new Reader(); var service = new BookDownloadService(new Projects(Snapshot(), Snapshot()), reader);
        await using (var book = await service.OpenAsync("project", 1, TestContext.Current.CancellationToken))
        {
            using var output = new MemoryStream(); await book.CopyToAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "a", "b" }, reader.Opened);
            Assert.Equal(48, book.Length);
            Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(output.ToArray().AsSpan(44)));
            Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(output.ToArray().AsSpan(46)));
        }
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
    }
    [Fact]
    public async Task Incomplete_book_is_rejected_without_opening_audio()
    {
        var reader = new Reader(); var service = new BookDownloadService(new Projects(Snapshot(missing:true)), reader);
        Assert.Equal("book.incomplete", (await Assert.ThrowsAsync<StudioException>(() => service.OpenAsync("project",1,TestContext.Current.CancellationToken))).Code);
        Assert.Empty(reader.Opened);
    }
    [Fact]
    public async Task Revision_changed_while_pinning_rejects_download_and_releases_handles()
    {
        var reader = new Reader(); var service = new BookDownloadService(new Projects(Snapshot(),Snapshot(2)),reader);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => service.OpenAsync("project",1,TestContext.Current.CancellationToken))).Code);
        Assert.All(reader.Streams, stream => Assert.False(stream.CanRead));
    }
    [Fact]
    public async Task Later_artifact_failure_releases_earlier_handles()
    {
        var reader = new Reader(failSecond:true); var service = new BookDownloadService(new Projects(Snapshot()),reader);
        await Assert.ThrowsAsync<StudioException>(() => service.OpenAsync("project",1,TestContext.Current.CancellationToken));
        Assert.Single(reader.Streams); Assert.False(reader.Streams[0].CanRead);
    }
    private sealed class Projects(params ProjectSnapshotDto[] snapshots) : IStudioProjectService
    {
        private int calls;
        public Task<ProjectSnapshotDto> GetAsync(string id,CancellationToken token=default) => Task.FromResult(snapshots[Math.Min(calls++,snapshots.Length-1)]);
        public Task<ProjectSnapshotDto> OpenAsync(string id,CancellationToken token=default) => throw new NotSupportedException();
        public Task<CreateProjectResult> CreateAsync(CreateProjectCommand command,string key,CancellationToken token=default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken token=default) => throw new NotSupportedException();
    }
    private sealed class Reader(bool failSecond=false) : IProjectArtifactReader
    {
        public List<string> Opened { get; }=[]; public List<MemoryStream> Streams { get; }=[];
        public Task<ArtifactReadHandle> OpenAsync(string projectId,string artifactId,CancellationToken token=default)
        {
            if (failSecond && Opened.Count==1) throw new StudioException("artifact.missing");
            Opened.Add(artifactId); byte[] bytes=new byte[46]; "RIFF"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),38); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16),16); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24),48000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28),96000); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32),2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34),16); "data"u8.CopyTo(bytes.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40),2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44),(short)(artifactId=="a"?1:2));
            var stream=new MemoryStream(bytes); Streams.Add(stream); return Task.FromResult(new ArtifactReadHandle(stream,46,"audio/wav","audio.wav"));
        }
    }
}
