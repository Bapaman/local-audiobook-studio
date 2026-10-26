using System.Text.Json;
using LocalAudiobookStudio.App.Hosting;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusLaunchTests
{
    [Fact]
    public void Explicit_corpus_config_composes_with_worker_suffix_without_creating_runtime()
    {
        using var temporary = new CorpusTemporaryDirectory();
        string config = temporary.WriteConfig();
        var dependencies = LocalAppHostDependencies.CreateForArguments([
            "--tts-corpus-runtime-config", config, "--developer-cpu-runtime",
            temporary.Child("runtime"), temporary.Child("model"), temporary.Child("lock")]);
        Assert.NotNull(dependencies);
        Assert.False(Directory.Exists(temporary.Child("workers")));
        Assert.False(File.Exists(temporary.Child("python.exe")));
    }

    [Fact]
    public void Corpus_suffix_can_follow_worker_suffix()
    {
        using var temporary = new CorpusTemporaryDirectory();
        var dependencies = LocalAppHostDependencies.CreateForArguments([
            "--developer-cpu-runtime", temporary.Child("runtime"), temporary.Child("model"), temporary.Child("lock"),
            "--tts-corpus-runtime-config", temporary.WriteConfig()]);
        Assert.NotNull(dependencies);
    }

    [Fact]
    public void Duplicate_corpus_option_is_rejected()
    {
        using var temporary = new CorpusTemporaryDirectory(); string config = temporary.WriteConfig();
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments([
            "--tts-corpus-runtime-config", config, "--tts-corpus-runtime-config", config]));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("relative")]
    [InlineData("empty-sites")]
    [InlineData("duplicate-sites")]
    [InlineData("unc")]
    public void Descriptor_rejects_ambiguous_or_unsafe_paths(string mutation)
    {
        using var temporary = new CorpusTemporaryDirectory(); string config = temporary.WriteConfig();
        string content = File.ReadAllText(config);
        using var document = JsonDocument.Parse(content);
        var values = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (mutation == "duplicate") content = content[..^1] + ",\"pythonPath\":\"C:/other/python.exe\"}";
        else
        {
            switch (mutation)
            {
                case "unknown": values["extra"] = JsonSerializer.SerializeToElement("value"); break;
                case "relative": values["pythonPath"] = JsonSerializer.SerializeToElement("python.exe"); break;
                case "empty-sites": values["dependencySites"] = JsonSerializer.SerializeToElement(Array.Empty<string>()); break;
                case "duplicate-sites": values["dependencySites"] = JsonSerializer.SerializeToElement(new[] { temporary.Child("site-packages"), temporary.Child("site-packages") }); break;
                case "unc": values["modelRoot"] = JsonSerializer.SerializeToElement("\\\\server\\share\\model"); break;
            }
            content = JsonSerializer.Serialize(values);
        }
        File.WriteAllText(config, content);
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--tts-corpus-runtime-config", config]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pythonPath\":\"relative.exe\"}")]
    [InlineData("{invalid")]
    public void Invalid_configuration_fails_before_runtime_creation(string content)
    {
        using var temporary = new CorpusTemporaryDirectory(); string config = temporary.Child("config.json");
        File.WriteAllText(config, content);
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--tts-corpus-runtime-config", config]));
    }
}

internal sealed class CorpusTemporaryDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "las-corpus-native-" + Guid.NewGuid().ToString("N"));
    internal CorpusTemporaryDirectory() => Directory.CreateDirectory(Root);
    internal string Child(string relative) => Path.Combine(Root, relative);
    internal string WriteConfig()
    {
        string path = Child("config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            pythonPath = Child("python.exe"), pythonBaseRoot = Child("python-base"),
            dependencySites = new[] { Child("site-packages") }, workerRoot = Child("workers"),
            modelRoot = Child("whisper"), modelManifestPath = Child("whisper.lock.json"),
            runtimeInventoryPath = Child("runtime-inventory.json")
        }));
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);
}
