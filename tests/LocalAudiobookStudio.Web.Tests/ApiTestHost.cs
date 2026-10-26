using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Lifetime;
using LocalAudiobookStudio.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly bool deleteRootOnDispose;
    private string cookies = "";
    private string csrf = "";
    public string Root { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => app.Services;
    public Task StopAsync(CancellationToken token) => app.StopAsync(token);
    public ExitControl Exit { get; } = new();
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    private ApiTestHost(string root, WebApplication app, HttpClient client, ExitControl exit, bool deleteRootOnDispose)
    { Root = root; this.app = app; Client = client; Exit = exit; this.deleteRootOnDispose = deleteRootOnDispose; }

    public static async Task<ApiTestHost> StartAsync(long maximumBytes = 1L << 30, string? root = null,
        Action<IServiceCollection>? configureServices = null, bool deleteRootOnDispose = true)
    {
        root ??= Path.Combine(Path.GetTempPath(), "las-api-" + Guid.NewGuid().ToString("N"));
        var exit = new ExitControl();
        var app = LocalWebHost.Build(new(root), services =>
        {
            services.AddSingleton(new VoiceRecordingPolicy(48000, 57600000, maximumBytes));
            services.AddSingleton<IAppLifetimeControl>(exit);
            configureServices?.Invoke(services);
        });
        await app.StartAsync(Token);
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new(address) };
        var host = new ApiTestHost(root, app, client, exit, deleteRootOnDispose);
        var ticket = app.Services.GetRequiredService<LaunchTokenStore>().Issue(TimeSpan.FromSeconds(60));
        using var launch = await client.GetAsync("/_session/launch?token=" + Uri.EscapeDataString(ticket.Plaintext), Token);
        host.cookies = Cookie(launch, "las_session=");
        using var bootstrap = await host.SendAsync(HttpMethod.Get, "/api/bootstrap");
        host.cookies += "; " + Cookie(bootstrap, ".AspNetCore.Antiforgery.");
        host.csrf = (await JsonAsync(bootstrap)).GetProperty("antiforgeryRequestToken").GetString()!;
        return host;
    }

    private static string Cookie(HttpResponseMessage response, string prefix)
        => Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith(prefix, StringComparison.Ordinal)).Split(';')[0];

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, string? revision = null, string? key = null)
        => Client.SendAsync(Request(method, path, content, revision, key), Token);

    public HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null, string? revision = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (csrf != "") request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (revision != null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        if (key != null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    public async Task<(string Id, string ETag)> CreateAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/projects", JsonContent.Create(new { displayName = "Synthetic book" }), key: Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await JsonAsync(response)).GetProperty("id").GetString()!, response.Headers.ETag!.Tag);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.Clone();
    }

    public static MultipartFormDataContent Multipart(byte[] bytes, string metadata = "{\"rightsConfirmed\":true}", bool extra = false, bool chunked = false)
    {
        var form = new MultipartFormDataContent("synthetic-boundary");
        form.Add(new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata");
        HttpContent file = chunked ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        form.Add(file, "file", "synthetic.wav");
        if (extra) form.Add(new ByteArrayContent([1]), "file", "extra.wav");
        return form;
    }

    public static HttpContent RawMultipart(byte[] bytes, string boundary = "synthetic-boundary")
    {
        var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", boundary) } };
        return content;
    }

    public static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    public static byte[] Wav(int samples = 480000)
    {
        byte[] bytes = new byte[44 + samples * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
        for (int i = 44; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), 1000);
        return bytes;
    }

    public void AssertNoParts() => Assert.Empty(Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.part", SearchOption.AllDirectories) : []);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await app.StopAsync(CancellationToken.None); await app.DisposeAsync();
        if (deleteRootOnDispose && Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    internal sealed class ExitControl : IAppLifetimeControl
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RequestShutdown() => Requested.TrySetResult();
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
