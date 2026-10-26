using System.Diagnostics;
using System.Reflection;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class BootstrapCrashTests
{
    [Fact]
    public async Task Killed_bootstrap_process_leaves_prior_attempt_untouched_and_retry_publishes_one_complete_database()
    {
        const string rootVariable = "LAS_BOOTSTRAP_TEST_ROOT", boundaryVariable = "LAS_BOOTSTRAP_TEST_BOUNDARY", kindVariable = "LAS_BOOTSTRAP_TEST_KIND";
        if (Environment.GetEnvironmentVariable(rootVariable) is { } childRoot)
        {
            string boundary = Environment.GetEnvironmentVariable(boundaryVariable)!;
            Action<string> observer = current =>
            {
                if (current != boundary) return;
                File.WriteAllText(Path.Combine(childRoot, "ready"), current);
                Thread.Sleep(Timeout.Infinite);
            };
            var catalog = new SqliteProjectCatalog(new(childRoot)); var core = new ProjectService();
            object observed = Environment.GetEnvironmentVariable(kindVariable) == "catalog" ? catalog : core;
            observed.GetType().GetField("lifecycleObserver", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(observed, observer);
            if (observed == catalog) await catalog.ListAsync(TestContext.Current.CancellationToken);
            else await new StudioProjectService(new(childRoot), catalog, core).CreateAsync(new("Synthetic"), "crash", TestContext.Current.CancellationToken);
            Assert.Fail("The selected crash boundary was not reached.");
            return;
        }
        foreach (string kind in new[] { "catalog", "project" })
        foreach (string boundary in new[] { "bootstrap-main-reserved", "bootstrap-marker-partial", "bootstrap-marker-committed",
                     "bootstrap-initialized", "bootstrap-checkpointed", "bootstrap-published", "bootstrap-schema-committed" })
        {
            if (kind == "catalog" && boundary == "bootstrap-schema-committed") continue;
            using var sandbox = new CatalogSandbox();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(BootstrapCrashTests).Assembly.Location);
            start.ArgumentList.Add("-method"); start.ArgumentList.Add("*Killed_bootstrap_process*");
            start.Environment[rootVariable] = sandbox.Root; start.Environment[boundaryVariable] = boundary; start.Environment[kindVariable] = kind;
            using var child = Process.Start(start)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken), errors = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                while (!File.Exists(Path.Combine(sandbox.Root, "ready")))
                {
                    if (child.HasExited) Assert.Fail($"Crash child exited ({kind}/{boundary}): {await output} {await errors}");
                    await Task.Delay(20, timeout.Token);
                }
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, errors).WaitAsync(timeout.Token);
                Assert.True(child.HasExited);
                var previous = await ReadReleasedAttemptsAsync(sandbox.Root, timeout.Token);
                if (kind == "catalog") Assert.Empty(await sandbox.Catalog().ListAsync(timeout.Token));
                else
                {
                    var receipt = (await sandbox.Catalog().FindReceiptAsync("crash", timeout.Token))!;
                    var retry = await sandbox.Service().CreateAsync(new("Synthetic"), "crash", timeout.Token);
                    Assert.Equal(receipt.ProjectId, retry.Snapshot.Id); Assert.Equal("Synthetic", retry.Snapshot.DisplayName);
                    Assert.False((await sandbox.Service().CreateAsync(new("Synthetic"), "crash", timeout.Token)).Created);
                    Assert.Single(await sandbox.Service().ListAsync(timeout.Token));
                    Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.Root, "projects")));
                }
                foreach (var file in previous) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
                await output; await errors;
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> ReadReleasedAttemptsAsync(string root, CancellationToken token)
    {
        // Process termination and filesystem handle release are separate observed
        // boundaries. Require simultaneous exclusive access before taking evidence;
        // never infer a scanner or leaked-process cause from one sharing violation.
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            var opened = new Dictionary<string, FileStream>();
            try
            {
                foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                             .Where(path => path.Contains(".studio-bootstrap", StringComparison.Ordinal)))
                    opened.Add(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
                return opened.ToDictionary(pair => pair.Key, pair =>
                {
                    using var copy = new MemoryStream(); pair.Value.CopyTo(copy); return copy.ToArray();
                });
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && waiting.Elapsed < TimeSpan.FromSeconds(3)) { }
            finally { foreach (var stream in opened.Values) stream.Dispose(); }
            await Task.Delay(20, token);
        }
    }
}
