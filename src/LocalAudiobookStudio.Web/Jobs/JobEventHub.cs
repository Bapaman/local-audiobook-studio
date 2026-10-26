using System.Threading.Channels;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Jobs;

/// <summary>Bounded non-blocking hints; SQLite remains the replay and snapshot authority.</summary>
public sealed class JobEventHub(IStudioJobService jobs, TimeProvider time, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly object sync = new();
    private readonly Dictionary<string, ProjectSubscriptions> projects = new(StringComparer.Ordinal);
    private int count;
    private bool closed;
    private CancellationTokenRegistration shutdown;

    public override Task StartAsync(CancellationToken token)
    {
        // GenericWebHost can drain Kestrel before calling this service's StopAsync.
        // End SSE first so a live browser cannot hold that drain open.
        shutdown = lifetime.ApplicationStopping.Register(CloseSubscriptions);
        return base.StartAsync(token);
    }

    public Subscription Subscribe(string projectId)
    {
        lock (sync)
        {
            if (closed) throw new ApiException(503, "events.unavailable");
            if (count == 8) throw new ApiException(429, "events.limit");
            if (!projects.TryGetValue(projectId, out var group)) projects[projectId] = group = new();
            var subscription = new Subscription(this, projectId, group);
            group.Subscribers.Add(subscription); count++; return subscription;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, token);
                (string Id, ProjectSubscriptions Group, long Cursor)[] scans;
                lock (sync) scans = projects.Where(x => x.Value.Cursor != null)
                    .Select(x => (x.Key, x.Value, x.Value.Cursor!.Value)).ToArray();
                foreach (var (id, group, cursor) in scans)
                {
                    JobEventPageDto page;
                    try { page = await jobs.ReadEventPageAsync(id, cursor, token); }
                    catch (StudioException) { continue; }
                    lock (sync)
                    {
                        if (!projects.TryGetValue(id, out var current) || !ReferenceEquals(current, group)) continue;
                        // A slower first baseline can lower the required cursor while
                        // this atomic read is in flight. Retry that gap; never overwrite it.
                        if (group.Cursor != cursor) continue;
                        if (page.Reset) foreach (var subscriber in group.Subscribers) subscriber.Reset();
                        else foreach (var item in page.Events)
                            foreach (var subscriber in group.Subscribers) subscriber.Publish(item);
                        group.Cursor = page.HeadSequence;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CloseSubscriptions()
    {
        lock (sync)
        {
            closed = true;
            foreach (var group in projects.Values) foreach (var subscriber in group.Subscribers) subscriber.Close();
        }
    }

    public override async Task StopAsync(CancellationToken token)
    { CloseSubscriptions(); await base.StopAsync(token); }

    public override void Dispose() { shutdown.Dispose(); base.Dispose(); }

    internal sealed class ProjectSubscriptions
    {
        public long? Cursor { get; set; }
        public HashSet<Subscription> Subscribers { get; } = [];
    }

    public sealed class Subscription : IDisposable
    {
        private readonly JobEventHub hub;
        private readonly string project;
        private readonly ProjectSubscriptions group;
        private readonly Channel<JobEventDto> channel = Channel.CreateBounded<JobEventDto>(new BoundedChannelOptions(32)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        private int reset;
        private bool disposed;
        internal Subscription(JobEventHub hub, string project, ProjectSubscriptions group)
        { this.hub = hub; this.project = project; this.group = group; }
        public ChannelReader<JobEventDto> Reader => channel.Reader;
        public bool ResetRequested => Volatile.Read(ref reset) != 0;
        public void EstablishBaseline(long head)
        {
            lock (hub.sync)
            {
                if (!disposed && (group.Cursor == null || head < group.Cursor)) group.Cursor = head;
            }
        }
        internal void Publish(JobEventDto hint)
        { if (!ResetRequested && !channel.Writer.TryWrite(hint)) Reset(); }
        internal void Reset() { Interlocked.Exchange(ref reset, 1); channel.Writer.TryComplete(); }
        internal void Close() => channel.Writer.TryComplete();
        public void Dispose()
        {
            lock (hub.sync)
            {
                if (disposed) return;
                disposed = true; Close(); group.Subscribers.Remove(this); hub.count--;
                if (group.Subscribers.Count == 0) hub.projects.Remove(project);
            }
        }
    }
}
