using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class UploadApiTests
{
    [Fact]
    public async Task Accepted_recording_anchor_and_opaque_range_read_work()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync(); byte[] wav = ApiTestHost.Wav();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(wav), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        string artifact = (await ApiTestHost.JsonAsync(upload)).GetProperty("voiceRecording").GetProperty("artifactId").GetString()!;
        using var full = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
        Assert.Equal(wav, await full.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var rangeRequest = host.Request(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
        rangeRequest.Headers.Range = new(44, 59);
        using var range = await host.Client.SendAsync(rangeRequest, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 44-59/{wav.Length}", range.Content.Headers.ContentRange!.ToString());
        Assert.Equal(wav[44..60], await range.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var anchor = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/anchor", JsonContent.Create(new { startMilliseconds = 0, endMilliseconds = 10000, transcript = "Синтетический текст.", pairListenedAndConfirmed = true }), upload.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, anchor.StatusCode);
        string anchorArtifact = (await ApiTestHost.JsonAsync(anchor)).GetProperty("voiceAnchor").GetProperty("artifactId").GetString()!;
        using var anchorRequest = host.Request(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{anchorArtifact}");
        anchorRequest.Headers.Range = new(44, 59);
        using var anchorRange = await host.Client.SendAsync(anchorRequest, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.PartialContent, anchorRange.StatusCode);
        Assert.Equal(wav[44..60], await anchorRange.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var forged = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        using var staging = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}.part");
        Assert.Equal(HttpStatusCode.NotFound, staging.StatusCode);
        host.AssertNoParts();
        await using var reopenedHost = await ApiTestHost.StartAsync(root: host.Root, deleteRootOnDispose: false);
        using var reopened = await reopenedHost.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/open", revision: "\"3\"");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("\"3\"", reopened.Headers.ETag!.Tag);
        Assert.Equal(anchorArtifact, (await ApiTestHost.JsonAsync(reopened)).GetProperty("voiceAnchor").GetProperty("artifactId").GetString());
        using var persisted = await reopenedHost.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
        Assert.Equal(wav, await persisted.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
    }

    [Fact]
    public async Task Extra_third_part_is_rejected_before_acceptance_and_cleans_staging()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav(), extra: true), project.ETag);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var get = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}");
        Assert.Equal(1, (await ApiTestHost.JsonAsync(get)).GetProperty("revision").GetInt64()); host.AssertNoParts();
    }

    [Fact]
    public async Task Chunked_actual_file_limit_rejects_without_mutation()
    {
        await using var host = await ApiTestHost.StartAsync(maximumBytes: 1000); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav(), chunked: true), project.ETag);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertUnchangedAsync(host, project.Id);
    }

    [Fact]
    public async Task Chunked_permitted_recording_exceeds_default_kestrel_limit()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav(16_000_000), chunked: true), project.ETag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); host.AssertNoParts();
    }

    [Fact]
    public async Task Text_file_uses_metadata_and_rejects_extra_part_before_acceptance()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/file", ApiTestHost.Multipart(Encoding.UTF8.GetBytes("Привет."), "{\"displayName\":\"Text\"}", extra: true), project.ETag);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var valid = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/file", ApiTestHost.Multipart(Encoding.UTF8.GetBytes("Привет."), "{\"displayName\":\"Text\"}"), project.ETag);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Theory]
    [InlineData("order", 400)]
    [InlineData("metadata-file", 400)]
    [InlineData("missing-file", 400)]
    [InlineData("unknown-name", 400)]
    [InlineData("truncated-terminal", 400)]
    [InlineData("metadata-size", 413)]
    [InlineData("header-size", 400)]
    [InlineData("header-count", 400)]
    [InlineData("boundary-size", 400)]
    public async Task Strict_multipart_grammar_and_bounds_reject_without_mutation(string kind, int status)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        string boundary = kind == "boundary-size" ? new string('b', 129) : "synthetic-boundary";
        string metadata = kind == "metadata-size" ? "{\"rightsConfirmed\":true,\"fileName\":\"" + new string('x', 17000) + "\"}" : "{\"rightsConfirmed\":true}";
        string metadataHeaders = "Content-Disposition: form-data; name=\"metadata\"" + (kind == "metadata-file" ? "; filename=\"bad.json\"" : "") + "\r\nContent-Type: application/json\r\n";
        if (kind == "header-size") metadataHeaders += "X-Large: " + new string('x', 17000) + "\r\n";
        if (kind == "header-count") metadataHeaders += string.Concat(Enumerable.Range(0, 17).Select(i => $"X-{i}: v\r\n"));
        byte[] first = Encoding.UTF8.GetBytes($"--{boundary}\r\n{metadataHeaders}\r\n{metadata}\r\n");
        string fileName = kind == "unknown-name" ? "other" : "file";
        byte[] file = [.. Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"{fileName}\"; filename=\"synthetic.wav\"\r\n\r\n"), .. ApiTestHost.Wav(), .. Encoding.ASCII.GetBytes("\r\n")];
        byte[] terminal = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
        byte[] body = kind switch
        {
            "order" => [.. file, .. first, .. terminal],
            "missing-file" => [.. first, .. terminal],
            "truncated-terminal" => [.. first, .. file, .. Encoding.ASCII.GetBytes($"--{boundary}-")],
            _ => [.. first, .. file, .. terminal]
        };
        using var content = ApiTestHost.RawMultipart(body, boundary);
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", content, project.ETag);
        Assert.Equal(status, (int)response.StatusCode);
        var problem = await ApiTestHost.JsonAsync(response);
        Assert.Equal(status == 413 ? "request.too_large" : "request.multipart.invalid", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic.wav", problem.ToString());
        Assert.DoesNotContain(host.Root, problem.ToString());
        await AssertUnchangedAsync(host, project.Id);
    }

    [Fact]
    public async Task Advertised_body_over_aggregate_transport_limit_is_rejected_before_reading()
    {
        await using var host = await ApiTestHost.StartAsync(maximumBytes: 1000); var project = await host.CreateAsync();
        using var content = new ByteArrayContent(new byte[1000 + 1024 * 1024 + 1]);
        content.Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "synthetic-boundary") } };
        using var request = host.Request(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", content, project.ETag);
        // This rejected unread body exceeds Kestrel's drain cap; do not pool its connection.
        request.Headers.ConnectionClose = true;
        using var response = await host.Client.SendAsync(request, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request.too_large", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
        await AssertUnchangedAsync(host, project.Id);
    }

    [Theory]
    [InlineData(1L << 30)]
    [InlineData(1000)]
    public async Task Chunked_oversized_epilogue_is_rejected_with_large_and_small_file_allowances(long maximumBytes)
    {
        await using var host = await ApiTestHost.StartAsync(maximumBytes); var project = await host.CreateAsync();
        using var form = ApiTestHost.Multipart(maximumBytes == 1000 ? [1] : ApiTestHost.Wav());
        byte[] body = [.. await form.ReadAsByteArrayAsync(ApiTestHost.Token), .. new byte[1024 * 1024 + 1]];
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.RawMultipart(body), project.ETag);
        // MultipartReader bounds its terminal epilogue before the aggregate cap.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(host, project.Id);
    }

    [Fact]
    public async Task Disconnect_during_file_stream_cleans_staging_before_acceptance()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token);
        using var content = new InterruptedUpload();
        using var request = host.Request(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", content, project.ETag);
        var sending = host.Client.SendAsync(request, cancellation.Token);
        await content.PrefixSent.Task.WaitAsync(TimeSpan.FromSeconds(5), ApiTestHost.Token);
        await ApiTestHost.EventuallyAsync(() => Directory.EnumerateFiles(host.Root, "*.part", SearchOption.AllDirectories).Any());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await sending; });
        await ApiTestHost.EventuallyAsync(() => !Directory.EnumerateFiles(host.Root, "*.part", SearchOption.AllDirectories).Any());
        await AssertUnchangedAsync(host, project.Id);
    }

    [Fact]
    public async Task Playback_leaves_core_ownership_free_and_abort_releases_actual_file_and_ancestry_pins()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav(16_000_000)), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        string artifact = (await ApiTestHost.JsonAsync(upload)).GetProperty("voiceRecording").GetProperty("artifactId").GetString()!;
        string lockPath = Path.Combine(host.Root, "projects", project.Id, "artifact-ownership.lock");
        string media = Assert.Single(Directory.EnumerateFiles(Path.Combine(host.Root, "projects", project.Id), "*.wav", SearchOption.AllDirectories));
        string parent = Path.GetDirectoryName(media)!;
        using var request = host.Request(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var lease = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Throws<IOException>(() => { using var writer = File.Open(media, FileMode.Open, FileAccess.Write, FileShare.Read); });
        Assert.False(HasDirectoryRenameAccess(parent));
        Assert.Throws<IOException>(() => Directory.Move(parent, parent + ".moved"));
        response.Dispose();
        await ApiTestHost.EventuallyAsync(() =>
        {
            try { using var writer = File.Open(media, FileMode.Open, FileAccess.Write, FileShare.Read); return HasDirectoryRenameAccess(parent); }
            catch (IOException) { return false; }
        });
        // Both owned media and its effective parent can be renamed after abort.
        Directory.Move(parent, parent + ".moved"); Directory.Move(parent + ".moved", parent);
        using var rangeRequest = host.Request(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
        rangeRequest.Headers.Range = new(0, 3);
        using var range = await host.Client.SendAsync(rangeRequest, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(Encoding.ASCII.GetBytes("RIFF"), await range.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
    }

    [Theory]
    [InlineData("hardlink", 409, "storage.unavailable")]
    [InlineData("reparse-parent", 404, "project.unavailable")]
    public async Task Accepted_media_aliases_and_reparse_ancestors_are_rejected_preserving_external_sentinel(string kind, int status, string code)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync(); byte[] wav = ApiTestHost.Wav();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(wav), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        string artifact = (await ApiTestHost.JsonAsync(upload)).GetProperty("voiceRecording").GetProperty("artifactId").GetString()!;
        string media = Assert.Single(Directory.EnumerateFiles(host.Root, "*.wav", SearchOption.AllDirectories));
        string external = Path.Combine(host.Root, "external-synthetic"); Directory.CreateDirectory(external);
        string sentinel = Path.Combine(external, Path.GetFileName(media)); await File.WriteAllBytesAsync(sentinel, wav, ApiTestHost.Token);
        string parent = Path.GetDirectoryName(media)!;
        if (kind == "hardlink") { File.Delete(media); Assert.True(CreateHardLinkW(media, sentinel, 0)); }
        else
        {
            Directory.Move(parent, parent + ".original");
            var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(parent); start.ArgumentList.Add(external);
            using var process = Process.Start(start)!; await process.WaitForExitAsync(ApiTestHost.Token); Assert.Equal(0, process.ExitCode);
        }
        try
        {
            using var response = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}/artifacts/{artifact}");
            Assert.Equal(status, (int)response.StatusCode);
            var problem = await ApiTestHost.JsonAsync(response);
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.DoesNotContain(host.Root, problem.ToString());
            Assert.Equal(wav, await File.ReadAllBytesAsync(sentinel, ApiTestHost.Token));
        }
        finally { if (kind == "reparse-parent") { Directory.Delete(parent); Directory.Move(parent + ".original", parent); } }
    }

    private static async Task AssertUnchangedAsync(ApiTestHost host, string id)
    {
        using var get = await host.SendAsync(HttpMethod.Get, $"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var snapshot = await ApiTestHost.JsonAsync(get);
        Assert.Equal(1, snapshot.GetProperty("revision").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("voiceRecording").ValueKind);
        host.AssertNoParts();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string filename, string existingFilename, nint security);

    // Results.File may close its content before the wrapper finally disposes
    // ancestry pins. Observe BOTH lifetimes, without moving/deleting in a poll.
    private static bool HasDirectoryRenameAccess(string path)
    {
        using var handle = CreateFileW(path, 0x10000, 7, 0, 3, 0x02200000, 0);
        if (!handle.IsInvalid) return true;
        Assert.Equal(32, Marshal.GetLastWin32Error()); return false;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    private sealed class InterruptedUpload : HttpContent
    {
        public TaskCompletionSource PrefixSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InterruptedUpload() => Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "synthetic-boundary") } };
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            byte[] prefix = Encoding.ASCII.GetBytes("--synthetic-boundary\r\nContent-Disposition: form-data; name=\"metadata\"\r\nContent-Type: application/json\r\n\r\n{\"rightsConfirmed\":true}\r\n--synthetic-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"synthetic.wav\"\r\n\r\n");
            await stream.WriteAsync(prefix, cancellationToken);
            await stream.WriteAsync(ApiTestHost.Wav().AsMemory(0, 500000), cancellationToken);
            await stream.FlushAsync(cancellationToken); PrefixSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
