using System.Diagnostics;
using System.Text.Json;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class NativeLauncherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static string FakeExecutable => Path.Combine(AppContext.BaseDirectory, "fake-native", "llama.exe");
    [Fact]
    public async Task Windows_argument_serializer_preserves_actual_child_argv()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = Start();
        string[] values = ["Ёж мо́ре", "\"quoted\"", "a\\\\\"b", "trailing\\", "", "& | $(secret)", new string('я', 200) + "\\file"];
        foreach (string value in values) start.ArgumentList.Add(value);
        using var child = WindowsNativeLauncher.Start(start);
        string json = (await child.StandardOutput.ReadLineAsync(Token))!;
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(json));
        Assert.Equal(0, await child.WaitForExitAsync(Token)); await child.StopTreeAsync();
    }
    [Fact]
    public async Task Closing_job_kills_immediately_spawned_child_and_grandchild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = Start(); start.ArgumentList.Add("-t"); start.ArgumentList.Add("tree");
        using var child = WindowsNativeLauncher.Start(start);
        await child.StandardOutput.ReadLineAsync(Token);
        string[] ids = (await child.StandardOutput.ReadLineAsync(Token))!.Split(',');
        using var descendant = Process.GetProcessById(int.Parse(ids[0])); using var grandchild = Process.GetProcessById(int.Parse(ids[1]));
        child.Dispose();
        await Task.WhenAll(descendant.WaitForExitAsync(Token), grandchild.WaitForExitAsync(Token)).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(descendant.HasExited); Assert.True(grandchild.HasExited);
    }
    internal static ProcessStartInfo Start() => new(FakeExecutable)
    { WorkingDirectory = Path.GetDirectoryName(FakeExecutable)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
}
