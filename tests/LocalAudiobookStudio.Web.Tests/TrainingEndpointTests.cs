using System.Net;
using System.Net.Http.Json;
using System.Text;
using LocalAudiobookStudio.Application.Training;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TrainingEndpointTests
{
    [Fact]
    public async Task Unconfigured_training_is_unavailable_and_returns_no_local_paths()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Get, "/api/training/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await ApiTestHost.JsonAsync(response);
        Assert.False(value.GetProperty("ready").GetBoolean());
        Assert.True(value.GetProperty("experimental").GetBoolean());
        Assert.Equal(2, value.GetProperty("cpuThreads").GetInt32());
        Assert.Equal("training.runtime.not_ready", value.GetProperty("diagnosticCode").GetString());
        Assert.DoesNotContain(host.Root, value.ToString());
        var project = await host.CreateAsync();
        using var start = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/training",
            JsonContent.Create(new { experimentalConfirmed = true }), project.ETag, "first");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
    }
    [Fact]
    public async Task Training_commands_are_strict_replayable_and_return_training_CAS_without_private_paths()
    {
        // Leave space for the same nested native publication paths as a real launch.
        string root = Path.Combine(Path.GetTempPath(), "lt-api-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string python = Path.Combine(root, "python.exe"), shell = Path.Combine(root, "pwsh.exe");
        await File.WriteAllTextAsync(python, "inert-python", ApiTestHost.Token); await File.WriteAllTextAsync(shell, "inert-shell", ApiTestHost.Token);
        await File.WriteAllTextAsync(Path.Combine(root, "Run-RvcWorkflow.py"), "inert-script", ApiTestHost.Token);
        await using var host = await ApiTestHost.StartAsync(root: root, configureServices: services =>
        {
            var options = new RvcTrainingOptions(python, shell, root, root, root, root, root);
            services.AddRvcTraining(options);
            services.AddSingleton(new TrainingRuntime(options, new InertInspector(python)));
            services.RemoveAll<IHostedService>(); // Exercise durable commands without any native dispatch.
        });
        var project = await host.CreateAsync();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav()), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode); string etag = upload.Headers.ETag!.Tag;
        string path = $"/api/projects/{project.Id}/training";
        using var malformed = await host.SendAsync(HttpMethod.Post, path, new StringContent("{\"experimentalConfirmed\":true,\"sourcePath\":\"private\"}", Encoding.UTF8, "application/json"), etag, "bad");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var queued = await host.SendAsync(HttpMethod.Post, path, JsonContent.Create(new { experimentalConfirmed = true }), etag, "start");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode); var initial = await ApiTestHost.JsonAsync(queued);
        string id = initial.GetProperty("id").GetString()!;
        Assert.Equal("queued", initial.GetProperty("state").GetString()); Assert.False(initial.GetProperty("canResume").GetBoolean());
        Assert.DoesNotContain(root, initial.ToString()); Assert.False(initial.TryGetProperty("outputRoot", out _));
        using var replay = await host.SendAsync(HttpMethod.Post, path, JsonContent.Create(new { experimentalConfirmed = true }), "\"0\"", "start");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(id, (await ApiTestHost.JsonAsync(replay)).GetProperty("id").GetString());
        using var stopped = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/stop", revision: queued.Headers.ETag!.Tag, key: "stop");
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode); Assert.Equal("paused", (await ApiTestHost.JsonAsync(stopped)).GetProperty("state").GetString());
        using var stale = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/resume", revision: "\"1\"", key: "stale");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await ApiTestHost.JsonAsync(stale); Assert.Equal("training", problem.GetProperty("conflictType").GetString());
        Assert.Equal("paused", problem.GetProperty("currentSnapshot").GetProperty("state").GetString());
        Assert.Equal(stopped.Headers.ETag!.Tag, stale.Headers.ETag!.Tag);
        using var unauthenticated = await host.Client.GetAsync("/api/training/readiness", ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }
    private sealed class InertInspector(string file) : ITrainingRuntimeInspector
    {
        public async Task<IReadOnlyList<TrainingRuntimeFile>> InspectAsync(RvcTrainingOptions options, CancellationToken token)
        {
            byte[] bytes = await File.ReadAllBytesAsync(file, token);
            return [new(file, bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)))];
        }
    }
}
