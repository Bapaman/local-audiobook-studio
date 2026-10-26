using LocalAudiobookStudio.Web.Lifetime;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.App.Lifetime;

internal sealed class HostLifetimeControl(IHostApplicationLifetime lifetime) : IAppLifetimeControl
{
    private int _shutdownRequested;

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
        {
            lifetime.StopApplication();
        }
    }
}
