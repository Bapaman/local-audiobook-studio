using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Application.Training;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class TrainingRuntimeTests
{
    [Theory]
    [InlineData(170, true)]
    [InlineData(171, false)]
    public void Native_output_path_reserves_space_for_nested_publication(int length, bool allowed)
    {
        string root = "C:\\" + new string('a', length - 3);
        if (allowed) TrainingPathPolicy.RequireOutputRoot(root);
        else Assert.Equal("training.path_too_long", Assert.Throws<LocalAudiobookStudio.Application.Errors.StudioException>(() => TrainingPathPolicy.RequireOutputRoot(root)).Code);
    }
    [Fact]
    public async Task Missing_inventory_file_is_not_ready_and_changed_import_is_not_reaccepted()
    {
        using var box = new CatalogSandbox();
        string module = Path.Combine(box.Root, "local_workflow.py");
        await File.WriteAllTextAsync(module, "original", TestContext.Current.CancellationToken);
        var inspector = new InertInspector(module);
        var runtime = new TrainingRuntime(new(module, module, box.Root, box.Root, box.Root, box.Root, box.Root), inspector);
        Assert.True((await runtime.GetAsync(TestContext.Current.CancellationToken)).Ready);
        await File.WriteAllTextAsync(module, "modified", TestContext.Current.CancellationToken);
        Assert.Equal("training.runtime.changed", (await runtime.GetAsync(TestContext.Current.CancellationToken)).DiagnosticCode);
        Assert.Equal(1, inspector.Calls);
        File.Delete(module);
        Assert.False((await runtime.GetAsync(TestContext.Current.CancellationToken)).Ready);
    }
    [Fact]
    public async Task Failed_deep_inspection_never_reports_ready()
    {
        using var box = new CatalogSandbox();
        var runtime = new TrainingRuntime(new("missing", "missing", box.Root, box.Root, box.Root, box.Root, box.Root), new InertInspector("missing"));
        Assert.False((await runtime.GetAsync(TestContext.Current.CancellationToken)).Ready);
    }
    public sealed class InertInspector(string file) : ITrainingRuntimeInspector
    {
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<TrainingRuntimeFile>> InspectAsync(RvcTrainingOptions options, CancellationToken token)
        {
            Calls++;
            byte[] bytes = await File.ReadAllBytesAsync(file, token);
            return [new(file, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))];
        }
    }
}
