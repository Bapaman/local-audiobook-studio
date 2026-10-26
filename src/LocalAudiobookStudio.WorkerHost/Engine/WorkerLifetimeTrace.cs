namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>Opt-in, operation-local test characterization; no production sink or private payload.</summary>
internal static class WorkerLifetimeTrace
{
    private static readonly AsyncLocal<Action<string, int?>?> Sink = new();
    internal static IDisposable Observe(Action<string, int?> observer)
    {
        var previous = Sink.Value; Sink.Value = observer; return new Scope(previous);
    }
    internal static void Report(string phase, int? ownedPid = null)
    {
        try { Sink.Value?.Invoke(phase, ownedPid); }
        catch { /* A diagnostic observer must never affect process/publication semantics. */ }
    }
    private sealed class Scope(Action<string, int?>? previous) : IDisposable
    { public void Dispose() => Sink.Value = previous; }
}
