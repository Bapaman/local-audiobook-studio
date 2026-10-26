using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LocalAudiobookStudio.App.Tests;

public sealed class WinExeAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_WinExe_isolated_root_second_launch_reuses_host_and_authenticated_exit_stops_owned_listener(bool configuredCpu)
    {
        string root = Path.Combine(Path.GetTempPath(), "las-winexe-" + Guid.NewGuid().ToString("N"));
        string pipeName = $"LAS.SyntheticLaunch.{Process.GetCurrentProcess().SessionId}.{Guid.NewGuid():N}";
        string runner = Path.Combine(root, "runner");
        string dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(runner);
        // This gate exercises the actual WinExe/static-root/lifetime contract independently
        // of npm. The production React bundle has its own Playwright and publish gates.
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(AppContext.BaseDirectory, file);
            if (relative.StartsWith("fake-native" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (!(file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || (Path.GetDirectoryName(relative) is "" && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                || relative is "LocalAudiobookStudio.App.exe" or "LocalAudiobookStudio.WorkerHost.exe")) continue;
            string destination = Path.Combine(runner, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        // The test harness is RID-neutral, but the actual WinExe deps graph is win-x64.
        // That graph probes its native SQLite dependency beside the executable, as in
        // the App build/publish output, not in the harness's runtimes subdirectory.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll"),
            Path.Combine(runner, "e_sqlite3.dll"));
        Directory.CreateDirectory(Path.Combine(runner, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(runner, "wwwroot", "index.html"),
            "<html lang=\"ru\"><script src=\"/assets/synthetic.js\"></script></html>", TestContext.Current.CancellationToken);
        string executable = Path.Combine(runner, "LocalAudiobookStudio.App.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await using var parent = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? owner = null;
        Process? secondary = null;
        try
        {
            Task<string> firstLaunch = ReadLaunchAsync(parent, timeout.Token);
            owner = Start(executable, dataRoot, pipeName, configuredCpu);
            string first = await firstLaunch;
            var origin = new Uri(first).GetLeftPart(UriPartial.Authority);
            using var client = new HttpClient { BaseAddress = new(origin) };
            using var initial = await client.GetAsync(first, timeout.Token);
            initial.EnsureSuccessStatusCode();
            string html = await initial.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("<html lang=\"ru\">", html, StringComparison.Ordinal);
            Assert.Contains("/assets/", html, StringComparison.Ordinal);
            Assert.Equal("/", initial.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.False(owner.HasExited);

            parent.Disconnect();
            Task<string> secondLaunch = ReadLaunchAsync(parent, timeout.Token);
            secondary = Start(executable, dataRoot, pipeName, configuredCpu);
            string second = await secondLaunch;
            await secondary.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, secondary.ExitCode);
            Assert.NotEqual(first, second);
            Assert.Equal(origin, new Uri(second).GetLeftPart(UriPartial.Authority));
            Assert.False(owner.HasExited);
            using var reopened = await client.GetAsync(second, timeout.Token);
            reopened.EnsureSuccessStatusCode();
            var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap", timeout.Token);
            var readiness = await client.GetFromJsonAsync<JsonElement>("/api/runtime/readiness", timeout.Token);
            Assert.False(readiness.GetProperty("ready").GetBoolean());
            Assert.Equal(configuredCpu, readiness.GetProperty("experimental").GetBoolean());
            Assert.Equal("worker.runtime.not_ready", readiness.GetProperty("diagnosticCode").GetString());
            Assert.Equal("q8-f16-cpu", readiness.GetProperty("profileId").GetString());
            Assert.DoesNotContain(dataRoot, readiness.ToString());
            using (var unauthenticated = new HttpClient { BaseAddress = new(origin) })
            using (var denied = await unauthenticated.GetAsync("/api/runtime/readiness", timeout.Token))
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
            { Content = JsonContent.Create(new { displayName = "Synthetic acceptance" }) };
            createRequest.Headers.Add("Origin", origin);
            createRequest.Headers.Add("X-CSRF-TOKEN", bootstrap.GetProperty("antiforgeryRequestToken").GetString());
            createRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var created = await client.SendAsync(createRequest, timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
            var project = await created.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var snapshot = await client.GetFromJsonAsync<JsonElement>("/api/projects/" + project.GetProperty("id").GetString(), timeout.Token);
            Assert.Equal("Synthetic acceptance", snapshot.GetProperty("displayName").GetString());
            Assert.Equal(1, snapshot.GetProperty("revision").GetInt64());
            Assert.Empty(snapshot.GetProperty("segments").EnumerateArray());
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/app/exit");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("X-CSRF-TOKEN", bootstrap.GetProperty("antiforgeryRequestToken").GetString());
            using var exit = await client.SendAsync(request, timeout.Token);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, exit.StatusCode);
            await exit.Content.ReadAsByteArrayAsync(timeout.Token);
            await owner.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, owner.ExitCode);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/healthz", timeout.Token));
            Assert.Equal("", await owner.StandardOutput.ReadToEndAsync(timeout.Token));
            Assert.Equal("", await owner.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            foreach (var process in new[] { secondary, owner })
            {
                if (process is null) continue;
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); }
                process.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Process Start(string executable, string root, string pipeName, bool configuredCpu)
    {
        var info = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (string argument in new[] { "--synthetic-data-root", root, "--synthetic-launch-pipe", pipeName }) info.ArgumentList.Add(argument);
        if (configuredCpu)
            foreach (string argument in new[] { "--developer-cpu-runtime", Path.Combine(root, "runtime"), Path.Combine(root, "model"), Path.Combine(root, "locks") })
                info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Synthetic process failed to start.");
    }

    private static async Task<string> ReadLaunchAsync(NamedPipeServerStream parent, CancellationToken token)
    {
        await parent.WaitForConnectionAsync(token);
        using var reader = new StreamReader(parent, Encoding.ASCII, leaveOpen: true);
        string launch = await reader.ReadLineAsync(token) ?? throw new InvalidOperationException("Synthetic launch channel closed.");
        Assert.InRange(launch.Length, 1, 2048);
        var uri = new Uri(launch);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal("/_session/launch", uri.AbsolutePath);
        return launch;
    }
}
