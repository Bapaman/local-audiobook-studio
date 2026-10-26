using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace LocalAudiobookStudio.App.Tests;

public sealed class IsolatedLaunchTests
{
    [Fact]
    public async Task Explicit_synthetic_pipe_sends_only_validated_launch_URL_to_local_parent()
    {
        string pipeName = $"LAS.SyntheticLaunch.{Process.GetCurrentProcess().SessionId}.{Guid.NewGuid():N}";
        string root = Path.Combine(Path.GetTempPath(), "las-pipe-" + Guid.NewGuid().ToString("N"));
        var dependencies = LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root, "--synthetic-launch-pipe", pipeName]);
        await using var parent = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = parent.WaitForConnectionAsync(timeout.Token);
        var sending = dependencies.Browser.OpenAsync(new Uri("http://127.0.0.1:12345/_session/launch?token=synthetic-ticket"), timeout.Token);
        await connection;
        using var reader = new StreamReader(parent, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("http://127.0.0.1:12345/_session/launch?token=synthetic-ticket", await reader.ReadLineAsync(timeout.Token));
        await sending;
        await Assert.ThrowsAsync<ArgumentException>(() => dependencies.Browser.OpenAsync(new Uri("https://remote.test/"), timeout.Token));
    }

    [Theory]
    [InlineData("\\\\remote\\pipe")]
    [InlineData("LAS.SyntheticLaunch.999999.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("LAS.SyntheticLaunch.0.неASCII")]
    [InlineData("arbitrary")]
    public void Invalid_pipe_or_session_is_rejected(string pipeName)
    {
        string root = Path.Combine(Path.GetTempPath(), "las-pipe-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root, "--synthetic-launch-pipe", pipeName]));
    }

    [Fact]
    public void Pipe_without_synthetic_root_is_rejected()
        => Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--synthetic-launch-pipe", "arbitrary"]));

    [Fact]
    public void Synthetic_root_is_explicit_and_has_stable_separate_instance_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-synthetic-" + Guid.NewGuid().ToString("N"));
        var first = LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root]);
        var same = LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root.ToUpperInvariant() + Path.DirectorySeparatorChar]);
        var forwardSlashes = LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root.Replace('\\', '/')]);
        var other = LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", root + "-other"]);
        Assert.Equal(Path.GetFullPath(root), first.Paths.Root);
        Assert.Equal(first.InstanceName, same.InstanceName);
        Assert.Equal(first.Paths.Root, forwardSlashes.Paths.Root);
        Assert.Equal(first.InstanceName, forwardSlashes.InstanceName);
        Assert.NotEqual(first.InstanceName, other.InstanceName);
        Assert.NotEqual(SingleInstanceCoordinator.DefaultInstanceName, first.InstanceName);
        Assert.Matches("^[A-Za-z0-9.]+$", first.InstanceName);
        Assert.Equal(SingleInstanceCoordinator.DefaultInstanceName, LocalAppHostDependencies.CreateForArguments([]).InstanceName);
        Assert.Equal(AppDataPaths.ForCurrentUser().Root, LocalAppHostDependencies.CreateForArguments([]).Paths.Root);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("--unknown", "C:\\synthetic")]
    [InlineData("--synthetic-data-root", "relative")]
    [InlineData("--synthetic-data-root", "C:\\")]
    [InlineData("--synthetic-data-root", "\\\\server\\share\\synthetic")]
    [InlineData("--synthetic-data-root", "//synthetic-server/synthetic-share/studio")]
    [InlineData("--synthetic-data-root", "C:\\synthetic:stream")]
    [InlineData("--synthetic-data-root", "C:\\synthetic.")]
    [InlineData("--synthetic-data-root", "C:\\synthetic ")]
    [InlineData("--synthetic-data-root", "C:\\SYNTH~1\\nested")]
    [InlineData("--synthetic-data-root", "C:\\NUL\\nested")]
    public void Invalid_options_are_rejected_before_host_or_browser_start(string option, string root)
        => Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments([option, root]));

    [Fact]
    public void Default_private_root_cannot_be_selected_as_synthetic()
        => Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", AppDataPaths.ForCurrentUser().Root]));

    [Fact]
    public void Default_private_root_alias_cannot_be_selected_as_synthetic()
        => Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--synthetic-data-root", AppDataPaths.ForCurrentUser().Root + "."]));
}
