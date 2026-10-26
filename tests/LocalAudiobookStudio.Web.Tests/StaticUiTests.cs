using System.Net;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class StaticUiTests
{
    [Fact]
    public async Task Explicit_webroot_serves_authenticated_index_and_assets_with_existing_CSP()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-static-" + Guid.NewGuid().ToString("N"));
        string assets = Path.Combine(root, "ui");
        Directory.CreateDirectory(assets);
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(assets, "index.html"), "<html lang=\"ru\">synthetic-ui</html>", token);
        await File.WriteAllTextAsync(Path.Combine(assets, "app.js"), "console.log('synthetic');", token);
        try
        {
            await using var app = LocalWebHost.Build(new(Path.Combine(root, "data"), WebRoot: assets));
            await app.StartAsync(token);
            string address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new(address) };
            using var denied = await client.GetAsync("/app.js", token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            string ticket = app.Services.GetRequiredService<LaunchTokenStore>().Issue(TimeSpan.FromSeconds(60)).Plaintext;
            using var launch = await client.GetAsync("/_session/launch?token=" + Uri.EscapeDataString(ticket), token);
            Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
            Assert.Equal("/", launch.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.Contains("synthetic-ui", await launch.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
            using var script = await client.GetAsync("/app.js", token);
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Contains("synthetic", await script.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
            string csp = Assert.Single(script.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("style-src 'self'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
            using var unknownApi = await client.GetAsync("/api/missing", token);
            Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
            await app.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }
}
