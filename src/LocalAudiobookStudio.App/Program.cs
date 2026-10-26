using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;

namespace LocalAudiobookStudio.App;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(
        args,
        () => LocalAppHostDependencies.CreateForArguments(args),
        () => new StartupErrorSink(AppDataPaths.ForCurrentUser().LogsRoot),
        CancellationToken.None);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<LocalAppHostDependencies> createDependencies,
        Func<StartupErrorSink> createErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            await LocalAppHost.RunAsync(args, createDependencies(), cancellationToken);
            return 0;
        }
        catch (Exception)
        {
            try
            {
                createErrors().Record(StartupEventCode.AppStartFailed);
            }
            catch (Exception)
            {
                // The fixed process status still communicates failure if local logging is unavailable.
            }

            try
            {
                Console.Error.WriteLine("app_start_failed");
            }
            catch (Exception)
            {
                // A closed standard-error stream must not expose the original exception.
            }

            return 1;
        }
    }
}
