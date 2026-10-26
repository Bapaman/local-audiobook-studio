using System.Diagnostics;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using Microsoft.Extensions.DependencyInjection;
using static LocalAudiobookStudio.Web.Tests.VoxCpmTrainingApiFixture;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class VoxCpmTrainingAcceptanceHelperTests
{
    [Fact]
    public async Task Helper_is_metadata_only_until_explicit_start_and_same_intent_replays_without_new_lineage()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host); var version = (await host.Services.GetRequiredService<IStudioTtsCorpusService>().GetAsync(p.Id, ApiTestHost.Token))!.Versions.Single();
        string script = Script(); Assert.True(File.Exists(script), "The bounded normal-HTTP acceptance helper is missing.");
        string output = Path.Combine(host.Root, "helper-evidence"); Directory.CreateDirectory(output);
        string session = Session(host, output); var service = host.Services.GetRequiredService<IStudioVoxCpmTrainingService>();
        var status = await Run(script, host, p.Id, version.Id, version.Sha256, session, output, "Status"); Assert.Equal(0, status.Code); Assert.Null(await service.GetAsync(p.Id, ApiTestHost.Token));
        Assert.DoesNotContain(host.Root, status.Out); Assert.DoesNotContain("antiforgeryRequestToken", status.Out);
        var start = await Run(script, host, p.Id, version.Id, version.Sha256, session, output, "Start", new('a', 32)); Assert.Equal(0, start.Code);
        var first = (await service.GetAsync(p.Id, ApiTestHost.Token))!; Assert.Equal("queued", first.State); Assert.Equal(0, first.GlobalStep);
        var replay = await Run(script, host, p.Id, version.Id, version.Sha256, session, output, "Start", new('a', 32)); Assert.Equal(0, replay.Code);
        Assert.Equal(first.Id, (await service.GetAsync(p.Id, ApiTestHost.Token))!.Id); Assert.Equal(first.Revision, (await service.GetAsync(p.Id, ApiTestHost.Token))!.Revision);
        Assert.NotEmpty(Directory.GetFiles(output, "receipt-*.json"));
    }
    [Fact]
    public async Task Different_expected_manifest_never_enqueues_training()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host);
        string output = Path.Combine(host.Root, "helper-evidence"); Directory.CreateDirectory(output);
        string script = Script(); Assert.True(File.Exists(script), "The bounded normal-HTTP acceptance helper is missing.");
        var result = await Run(script, host, p.Id, p.VersionId, new('0', 64), Session(host, output), output, "Start", new('b', 32));
        Assert.NotEqual(0, result.Code); Assert.Null(await host.Services.GetRequiredService<IStudioVoxCpmTrainingService>().GetAsync(p.Id, ApiTestHost.Token));
    }
    private static string Script()
    {
        for (string? path = AppContext.BaseDirectory; path != null; path = Path.GetDirectoryName(path))
            if (File.Exists(Path.Combine(path, "LocalAudiobookStudio.sln"))) return Path.Combine(path, "scripts", "voxcpm-training", "acceptance.ps1");
        throw new InvalidOperationException("Repository unavailable.");
    }
    private static string Session(ApiTestHost host, string root)
    {
        using var request = host.Request(HttpMethod.Get, "/api/bootstrap"); string path = Path.Combine(root, "private-session.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { format = 1, baseUrl = host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority),
            cookieHeader = string.Join("; ", request.Headers.GetValues("Cookie")), csrfToken = request.Headers.GetValues("X-CSRF-TOKEN").Single() })); return path;
    }
    private static async Task<(int Code, string Out)> Run(string script, ApiTestHost host, string projectId, string versionId, string sha,
        string session, string output, string action, string? key = null)
    {
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-File", script, "-BaseUrl", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority),
            "-ProjectId", projectId, "-CorpusVersionId", versionId, "-ExpectedManifestSha256", sha, "-SessionPath", session, "-OutputDirectory", output, "-Action", action }) start.ArgumentList.Add(arg);
        if (key != null) { start.ArgumentList.Add("-OperationKey"); start.ArgumentList.Add(key); start.ArgumentList.Add("-ExperimentalConfirmed"); }
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(ApiTestHost.Token); var stderr = process.StandardError.ReadToEndAsync(ApiTestHost.Token);
        await process.WaitForExitAsync(ApiTestHost.Token); string text = await stdout; string error = await stderr;
        if (process.ExitCode == 0) Assert.True(string.IsNullOrWhiteSpace(error), "Successful helper printed unexpected diagnostics.");
        return (process.ExitCode, text);
    }
}
