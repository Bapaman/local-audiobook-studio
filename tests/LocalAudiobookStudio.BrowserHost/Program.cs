using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Lifetime;
using LocalAudiobookStudio.Web.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Test-only parent pipe protocol. Never used by the WinExe or its normal shortcuts.
if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0]) || args.Length == 2 && args[1] is not ("--legacy" or "--generation")) return 1;
string root = Path.Combine(Path.GetTempPath(), "las-browser-" + Guid.NewGuid().ToString("N"));
int exitCode = 0;
try
{
    if (args.Length == 2 && args[1] == "--legacy")
    {
        string id = Guid.NewGuid().ToString("N");
        await using var project = await new ProjectService().CreateAsync(Path.Combine(root, "projects", id), "Старый проект", id);
        var state = project.State; var now = DateTimeOffset.UtcNow;
        await project.SaveAsync(state with
        {
            Voice = new("voice", "voice/legacy.wav", "Старый голос", "hash", "hash", "hash", true, now),
            Chapters = [new("chapter", 0, "Глава", "Старый исходник.", "Старый исходник.", "Старый исходник.", now)],
            Segments = [new("segment", "chapter", 0, "Старый исходник.", "Старый исходник.", "Старая правка.", "hash", "fingerprint", null, null, 1, state.Metadata.Versions, state.Metadata.Parameters, "pending", false, now, now, null)]
        });
    }
    await using var app = LocalWebHost.Build(new(root, WebRoot: args[0]), services =>
    {
        services.AddSingleton(new VoiceRecordingPolicy(48000, 57600000));
        services.AddSingleton<IAppLifetimeControl, BrowserExit>();
        if (args.Length == 2 && args[1] == "--generation") SyntheticGeneration.Add(services);
    });
    await app.StartAsync();
    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    string ticket = app.Services.GetRequiredService<LaunchTokenStore>().Issue(TimeSpan.FromSeconds(60)).Plaintext;
    Console.WriteLine(address + "/_session/launch?token=" + Uri.EscapeDataString(ticket));
    Console.Out.Flush();
    // EOF is the parent disappearing; either EOF or an explicit line tears down the owned host.
    await Console.In.ReadLineAsync();
    await app.StopAsync();
}
catch { Console.Error.WriteLine("browser_host_failed"); exitCode = 1; }
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
    catch { Console.Error.WriteLine("browser_host_cleanup_failed"); exitCode = 1; }
}
return exitCode;

sealed class BrowserExit(IHostApplicationLifetime lifetime) : IAppLifetimeControl
{
    public void RequestShutdown() => lifetime.StopApplication();
}
