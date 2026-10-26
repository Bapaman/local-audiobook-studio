using LocalAudiobookStudio.Web.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class LocalWebHostTests
{
    [Fact]
    public async Task Host_binds_only_random_ipv4_loopback()
    {
        await using var host = await ApiTestHost.StartAsync(configureServices: services => Assert.Contains(
            services, service => service.ServiceType == typeof(LocalWebHostOptions)));
        var uri = host.Client.BaseAddress!;

        Assert.Equal("http", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.InRange(uri.Port, 1, 65535);
        Assert.Equal($"http://127.0.0.1:{uri.Port}/", uri.AbsoluteUri);
    }

    [Fact]
    public async Task Health_endpoint_returns_ready_without_exposing_an_api()
    {
        await using var host = await ApiTestHost.StartAsync();
        var client = host.Client;

        using var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.True(health.IsSuccessStatusCode);
        Assert.Equal("ready", await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var unknown = await client.GetAsync("/api/never", TestContext.Current.CancellationToken);
        Assert.False(unknown.IsSuccessStatusCode);
    }
}
