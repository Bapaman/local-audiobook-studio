using System.Diagnostics;
using System.Globalization;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new WorkerException("worker.unsupported.platform");
            var config = new Dictionary<string, string>(StringComparer.Ordinal); bool developer = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--developer-mode") { if (developer) throw new WorkerException("worker.configuration"); developer = true; continue; }
                if (i + 1 >= args.Length || !config.TryAdd(args[i], args[++i])) throw new WorkerException("worker.configuration");
            }
            string[] required = ["--pipe-name", "--project-root", "--runtime-root", "--model-root", "--lock-root", "--parent-pid", "--parent-start-utc-ticks", "--timeout-seconds"];
            if (config.Count != required.Length || required.Any(key => !config.ContainsKey(key))) throw new WorkerException("worker.configuration");
            foreach (string key in required.Where(key => key.EndsWith("-root", StringComparison.Ordinal)))
                if (!Path.IsPathFullyQualified(config[key])) throw new WorkerException("worker.configuration");
            if (!int.TryParse(config["--parent-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
                !long.TryParse(config["--parent-start-utc-ticks"], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0 ||
                !int.TryParse(config["--timeout-seconds"], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 86400)
                throw new WorkerException("worker.configuration");
            string secret = Environment.GetEnvironmentVariable("LAS_WORKER_SESSION_SECRET") ?? throw new WorkerException("worker.configuration");
            Environment.SetEnvironmentVariable("LAS_WORKER_SESSION_SECRET", null);
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks || parent.HasExited) throw new WorkerException("worker.configuration");
            using var stop = new CancellationTokenSource();
            var resolver = new ArtifactResolver(config["--runtime-root"], config["--model-root"], config["--lock-root"], developer);
            await using var runner = new VoxCpmProcessRunner(config["--project-root"], resolver, TimeSpan.FromSeconds(seconds));
            var server = new WorkerPipeServer(config["--pipe-name"], secret, runner);
            Task watch = ObserveParent(parent, stop);
            try { await server.RunAsync(stop.Token); }
            finally { stop.Cancel(); await watch; }
            return 0;
        }
        catch (WorkerException e) { Console.Error.WriteLine(e.Code); return e.Code == "worker.unsupported.platform" ? 3 : 2; }
        catch (Exception) { Console.Error.WriteLine("worker.configuration"); return 2; }
    }
    private static async Task ObserveParent(Process parent, CancellationTokenSource stop)
    {
        try { await parent.WaitForExitAsync(stop.Token); stop.Cancel(); }
        catch (OperationCanceledException) { }
    }
}
