using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class WorkerCleanupObservationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Exact_owned_rate_fixture_records_disposal_and_immediate_delete_readiness_without_retry()
    {
        using var fixture = new VoxCpmProcessRunnerTests.RunnerFixture();
        string fixtureId = Guid.NewGuid().ToString("N"), operationId = Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        var observed = new ConcurrentQueue<(string Phase, int? Pid, double Ms)>();
        Process? wrapper = null;
        using var observation = WorkerLifetimeTrace.Observe((phase, pid) =>
        {
            observed.Enqueue((phase, pid, clock.Elapsed.TotalMilliseconds));
            if (phase == "native_started" && pid is { } ownedPid) wrapper = Process.GetProcessById(ownedPid);
        });
        try
        {
            var error = await Assert.ThrowsAsync<WorkerException>(() => fixture.Runner().SynthesizeAsync(fixture.Command("rate"), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal("worker.output.invalid", error.Code);
            Assert.NotNull(wrapper); Assert.True(wrapper.HasExited);
            wrapper.Dispose(); wrapper = null;
            observed.Enqueue(("test_wrapper_disposed", null, clock.Elapsed.TotalMilliseconds));
            string root = Path.GetFullPath(fixture.Artifacts.Root);
            string target = Path.GetFullPath(Path.Combine(fixture.Artifacts.Runtime, "bin", "voxcpm2-cli.exe"));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("voxcpm2-cli.exe", Path.GetFileName(target));
            // This is the copied executable owned by THIS fixture, never the shared
            // fake-native source, a historical directory, user audio or real runtime.
            string nativePath = @"\\?\" + target;
            int openError;
            using (var probe = CreateFileW(nativePath, 0x80010080, 0, 0, 3, 0x00200000, 0))
                openError = probe.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            double probeMs = clock.Elapsed.TotalMilliseconds;
            bool deleted = DeleteFileW(nativePath);
            int deleteError = deleted ? 0 : Marshal.GetLastWin32Error();
            double deleteMs = clock.Elapsed.TotalMilliseconds;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                fixtureId, operationId,
                milestones = observed.Select(x => new { phase = x.Phase, pid = x.Pid, ms = x.Ms }),
                probeMs, openError, deleteMs, deleteError, deleted,
                interpretation = openError == 0 && deleteError == 0 ? "immediate-success-inconclusive" : "immediate-failure-retained"
            }));
            Assert.Equal(0, openError); Assert.Equal(0, deleteError); Assert.True(deleted);
            foreach (string phase in new[] { "native_started", "root_exit", "job_zero_completion", "drains_completed", "native_disposed", "artifacts_disposed", "test_wrapper_disposed" })
                Assert.Single(observed, x => x.Phase == phase);
            Assert.True(observed.Single(x => x.Phase == "native_disposed").Ms <= observed.Single(x => x.Phase == "artifacts_disposed").Ms);
            Assert.True(observed.Single(x => x.Phase == "artifacts_disposed").Ms <= probeMs);
        }
        finally { wrapper?.Dispose(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint attributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteFileW(string path);
}
