using System.Text.Json;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TrainingLaunchTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "las-training-launch-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public async Task Explicit_training_config_is_parsed_without_starting_or_creating_runtime()
    {
        using var temporary = new TemporaryDirectory();
        string config = Path.Combine(temporary.Path, "training-runtime.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            pythonPath = Path.Combine(temporary.Path, "python.exe"),
            powerShellPath = Path.Combine(temporary.Path, "pwsh.exe"),
            scriptsRoot = Path.Combine(temporary.Path, "scripts"),
            decoderRoot = Path.Combine(temporary.Path, "decoder"),
            sourceRoot = Path.Combine(temporary.Path, "source"),
            baseAssetRoot = Path.Combine(temporary.Path, "base"),
            featureAssetRoot = Path.Combine(temporary.Path, "features")
        }), TestContext.Current.CancellationToken);
        var dependencies = LocalAppHostDependencies.CreateForArguments(["--training-runtime-config", config]);
        Assert.Equal(SingleInstanceCoordinator.DefaultInstanceName, dependencies.InstanceName);
        Assert.Equal(Path.Combine(temporary.Path, "python.exe"), dependencies.TrainingOptions!.PythonPath);
        Assert.Equal(Path.Combine(temporary.Path, "features"), dependencies.TrainingOptions.FeatureAssetRoot);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "python.exe")));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "scripts")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pythonPath\":\"relative.exe\"}")]
    [InlineData("{invalid")]
    public async Task Invalid_runtime_config_is_rejected_before_launch(string content)
    {
        using var temporary = new TemporaryDirectory();
        string config = Path.Combine(temporary.Path, "invalid.json");
        await File.WriteAllTextAsync(config, content, TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--training-runtime-config", config]));
    }
}
