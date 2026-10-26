using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class VoxCpmProcessRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Root_escape_is_rejected_before_any_native_output()
    {
        using var test = new RunnerFixture();
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command() with { FinalPath = "../outside.wav" }, _ => { }, Token));
        Assert.Equal("worker.path", error.Code); Assert.False(File.Exists(test.Part));
    }
    [Fact]
    public async Task Reparse_swap_before_publication_cannot_mutate_external_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var test = new RunnerFixture(); string segments = Path.GetDirectoryName(test.Final)!;
        string original = segments + "-original", external = Path.Combine(test.Artifacts.Root, "external"); Directory.CreateDirectory(external);
        bool swapped = false;
        try
        {
            var running = test.Runner().SynthesizeAsync(test.Command(), p =>
            {
                if (p.Phase != "validatingOutput") return;
                Directory.Move(segments, original);
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                foreach (string arg in new[] { "/c", "mklink", "/J", segments, external }) start.ArgumentList.Add(arg);
                using var junction = Process.Start(start)!; junction.WaitForExit();
                Assert.Equal(0, junction.ExitCode); swapped = true;
            }, Token);
            Assert.Equal("worker.cleanup", (await Assert.ThrowsAsync<WorkerException>(() => running)).Code);
            Assert.Empty(Directory.GetFileSystemEntries(external)); Assert.False(File.Exists(test.Final));
            Assert.True(File.Exists(Path.Combine(original, Path.GetFileName(test.Part))));
        }
        finally { if (swapped) Directory.Delete(segments); }
    }
    [Fact]
    public async Task Cancellation_before_publication_wins_and_no_final_is_created()
    {
        using var test = new RunnerFixture(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(), p => { if (p.Phase == "publishing") cancel.Cancel(); }, cancel.Token));
        Assert.Equal("worker.canceled", error.Code); Assert.False(File.Exists(test.Part)); Assert.False(File.Exists(test.Final));
    }
    [Fact]
    public async Task Publication_winner_is_reported_as_success_even_if_cancel_arrives_after_move()
    {
        using var test = new RunnerFixture(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await test.Runner(publication: new CancelAfterMove(cancel)).SynthesizeAsync(test.Command(), _ => { }, cancel.Token);
        Assert.Equal(test.Command().FinalPath, result.FinalPath); Assert.True(File.Exists(test.Final)); Assert.False(File.Exists(test.Part));
    }
    [Fact]
    public async Task Cancellation_inside_publishing_hook_prevents_rename()
    {
        using var test = new RunnerFixture(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner(publication: new CancelDuringPublishing(cancel))
            .SynthesizeAsync(test.Command(), _ => { }, cancel.Token));

        Assert.Equal("worker.canceled", error.Code);
        Assert.False(File.Exists(test.Final));
        Assert.False(File.Exists(test.Part));
    }

    [Fact]
    public async Task Staging_swap_during_publication_cannot_replace_the_inspected_file()
    {
        using var test = new RunnerFixture(); var publication = new SwapDuringPublication();

        var result = await test.Runner(publication: publication).SynthesizeAsync(test.Command(), _ => { }, Token);
        var published = await WavInspector.InspectAsync(test.Final, Token);

        Assert.False(publication.SwapSucceeded);
        Assert.Equal(result.Sha256, published.Sha256.ToLowerInvariant());
    }
    [Fact]
    public async Task Staging_swap_after_inspection_is_blocked_and_metadata_matches_final_file()
    {
        using var test = new RunnerFixture(); bool swapped = false;

        var result = await test.Runner().SynthesizeAsync(test.Command(), progress =>
        {
            if (progress.Phase != "publishing" || swapped) return;
            try
            {
                File.Move(test.Part, test.Part + ".inspected");
                WriteValidWav(test.Part, 2345); swapped = true;
            }
            catch (IOException) { }
        }, Token);

        Assert.False(swapped);
        Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
    }
    [Fact]
    public async Task Identical_byte_replacement_cannot_change_the_inspected_file_identity()
    {
        using var test = new RunnerFixture();
        string inspected = test.Part + ".inspected";
        bool swapped = false;

        var result = await test.Runner().SynthesizeAsync(test.Command(), progress =>
        {
            if (progress.Phase != "publishing") return;
            try
            {
                File.Move(test.Part, inspected);
                File.Copy(inspected, test.Part);
                swapped = true;
            }
            catch (IOException) { }
        }, Token);

        Assert.False(swapped);
        Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
    }
    [Fact]
    public async Task Pre_pin_replacement_is_neither_published_nor_deleted_by_cleanup()
    {
        using var test = new RunnerFixture();
        string original = test.Part + ".owned";
        bool replaced = false;

        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(), progress =>
        {
            if (progress.Phase != "validatingOutput") return;
            File.Move(test.Part, original);
            File.Copy(original, test.Part);
            replaced = true;
        }, Token));

        Assert.True(replaced);
        Assert.Equal("worker.output.invalid", error.Code);
        Assert.False(File.Exists(test.Final));
        Assert.True(File.Exists(test.Part));
        Assert.True(File.Exists(original));
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(test.Part));
    }
    [Fact]
    public async Task Flush_boundary_cannot_replace_the_reserved_file()
    {
        using var test = new RunnerFixture();
        var publication = new SwapAtFlush();

        var result = await test.Runner(publication: publication).SynthesizeAsync(test.Command(), _ => { }, Token);

        Assert.False(publication.SwapSucceeded);
        Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
    }
    [Fact]
    public async Task Existing_final_created_during_publication_is_preserved()
    {
        using var test = new RunnerFixture();
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner(publication: new CreateFinalDuringPublication()).SynthesizeAsync(test.Command(), _ => { }, Token));

        Assert.Equal("worker.storage", error.Code);
        Assert.Equal("already here", File.ReadAllText(test.Final));
        Assert.False(File.Exists(test.Part));
    }
    [Fact]
    public async Task Hard_link_to_staging_is_rejected_before_publication()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var test = new RunnerFixture();
        string alias = test.Part + ".alias";
        try
        {
            var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(), progress =>
            {
                if (progress.Phase == "publishing") Assert.True(CreateHardLinkW(alias, test.Part, nint.Zero));
            }, Token));

            Assert.Equal("worker.output.invalid", error.Code);
            Assert.False(File.Exists(test.Final));
            Assert.False(File.Exists(test.Part));
            Assert.True(File.Exists(alias));
        }
        finally { File.Delete(alias); }
    }
    [Fact]
    public async Task Directory_in_staging_name_is_never_published()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var test = new RunnerFixture();
        string inspected = test.Part + ".inspected";
        try
        {
            var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(), progress =>
            {
                if (progress.Phase != "validatingOutput") return;
                File.Move(test.Part, inspected);
                Directory.CreateDirectory(test.Part);
            }, Token));

            Assert.Equal("worker.storage", error.Code);
            Assert.False(File.Exists(test.Final));
            Assert.True(Directory.Exists(test.Part));
        }
        finally { if (Directory.Exists(test.Part)) Directory.Delete(test.Part); File.Delete(inspected); }
    }
    [Fact]
    public async Task Parent_directory_cannot_move_while_output_is_pinned()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var test = new RunnerFixture();
        var publication = new TryMoveParentDuringPublication();

        var result = await test.Runner(publication: publication).SynthesizeAsync(test.Command(), _ => { }, Token);

        Assert.False(publication.MoveSucceeded);
        Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
    }
    private sealed class CreateFinalDuringPublication : IOutputPublication
    {
        public void Staged(string part) { }
        public void BeforeFlush(string part) { }
        public void Publishing(string part, string final) => File.WriteAllText(final, "already here");
        public void Published(string final) { }
    }
    private sealed class SwapAtFlush : IOutputPublication
    {
        public bool SwapSucceeded { get; private set; }
        public void Staged(string part) { }
        public void BeforeFlush(string part)
        {
            try
            {
                string original = part + ".owned";
                File.Move(part, original);
                File.Copy(original, part);
                SwapSucceeded = true;
            }
            catch (IOException) { }
        }
        public void Publishing(string part, string final) { }
        public void Published(string final) { }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);
    private sealed class TryMoveParentDuringPublication : IOutputPublication
    {
        public bool MoveSucceeded { get; private set; }
        public void Staged(string part) { }
        public void BeforeFlush(string part) { }
        public void Publishing(string part, string final)
        {
            try { Directory.Move(Path.GetDirectoryName(part)!, Path.GetDirectoryName(part)! + "-moved"); MoveSucceeded = true; }
            catch (IOException) { }
        }
        public void Published(string final) { }
    }
    private sealed class CancelAfterMove(CancellationTokenSource cancel) : IOutputPublication
    {
        public void Staged(string part) { }
        public void BeforeFlush(string part) { }
        public void Publishing(string part, string final) { }
        public void Published(string final) => cancel.Cancel();
    }
    private sealed class CancelDuringPublishing(CancellationTokenSource cancel) : IOutputPublication
    {
        public void Staged(string part) { }
        public void BeforeFlush(string part) { }
        public void Publishing(string part, string final) => cancel.Cancel();
        public void Published(string final) { }
    }
    private sealed class SwapDuringPublication : IOutputPublication
    {
        public bool SwapSucceeded { get; private set; }
        public void Staged(string part) { }
        public void BeforeFlush(string part) { }
        public void Publishing(string part, string final)
        {
            try
            {
                File.Move(part, part + ".inspected");
                WriteValidWav(part, 1234);
                SwapSucceeded = true;
            }
            catch (IOException) { }
        }
        public void Published(string final) { }
    }
    private static void WriteValidWav(string path, short sample)
    {
        using var output = File.Create(path); using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(48000); writer.Write(96000); writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(4); writer.Write(sample); writer.Write((short)-sample);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancel_and_timeout_kill_native_descendants_before_cleaning_staging(bool timeout)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var test = new RunnerFixture(); string eventName = "Local\\las-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = test.Runner(TimeSpan.FromSeconds(timeout ? 2 : 10)).SynthesizeAsync(test.Command("tree-" + eventName), _ => { }, cancel.Token);
        Assert.True(await Task.Run(() => ready.WaitOne(5000), Token));
        string[] ids = File.ReadAllText(test.Part + ".pids").Split(',');
        using var main = Process.GetProcessById(int.Parse(ids[0])); using var child = Process.GetProcessById(int.Parse(ids[1])); using var grandchild = Process.GetProcessById(int.Parse(ids[2]));
        if (!timeout) cancel.Cancel();
        var error = await Assert.ThrowsAsync<WorkerException>(() => running);
        Assert.Equal(timeout ? "worker.timeout" : "worker.canceled", error.Code);
        Assert.True(main.HasExited); Assert.True(child.HasExited); Assert.True(grandchild.HasExited); Assert.False(File.Exists(test.Part)); Assert.False(File.Exists(test.Final));
    }
    [Fact]
    public async Task Native_success_publishes_valid_audio_and_preserves_exact_argv_under_non_invariant_culture()
    {
        using var test = new RunnerFixture();
        var command = test.Command("Ёж «мо́ре» & | $(x) \\\"", "Точный \"текст\"\\  \t\r\n");
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var phases = new List<string>(); var result = await test.Runner().SynthesizeAsync(command, p => phases.Add(p.Phase), Token);
            Assert.Equal(command.FinalPath, result.FinalPath); Assert.Equal(48, result.Length); Assert.Equal(48000, result.SampleRate);
            Assert.Equal(1, result.Channels); Assert.Equal(16, result.BitsPerSample); Assert.False(File.Exists(test.Part));
            Assert.Contains("generating", phases); Assert.Contains("publishing", phases);
            string[] argv = JsonSerializer.Deserialize<string[]>(File.ReadAllText(test.Part + ".argv"))!;
            Assert.Equal(new[] { "-t", command.Text, "--prompt-wav", test.Prompt, "--prompt-text", "Точный \"текст\"\\ ", "-o", test.Part, "--steps", "70", "--timesteps", "10", "--cfg", "2.0", "--temperature", "1.0", "--seed", "42", "--n-gpu-layers", "16", Path.Combine(test.Artifacts.Model, "VoxCPM2-BaseLM-Q8_0.gguf"), Path.Combine(test.Artifacts.Model, "VoxCPM2-Acoustic-F16.gguf") }, argv);
            Assert.True(test.Final.Length > 180); Assert.Equal("Точный \"текст\"\\  \t\r\n", command.Transcript);
            Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
    [Fact]
    public async Task Handle_publication_supports_paths_longer_than_legacy_max_path()
    {
        using var test = new RunnerFixture(180);
        Assert.True(test.Final.Length > 260);

        var result = await test.Runner().SynthesizeAsync(test.Command(), _ => { }, Token);

        Assert.True(File.Exists(test.Final));
        Assert.Equal(result.Sha256, (await WavInspector.InspectAsync(test.Final, Token)).Sha256.ToLowerInvariant());
    }
    [Theory]
    [InlineData("missing", "worker.output.missing")] [InlineData("corrupt", "worker.output.invalid")]
    [InlineData("stereo", "worker.output.invalid")] [InlineData("rate", "worker.output.invalid")]
    [InlineData("clipped", "worker.output.invalid")] [InlineData("crash", "worker.crash")] [InlineData("oom", "worker.cuda.oom")]
    public async Task Invalid_native_output_never_publishes_and_errors_contain_only_safe_codes(string mode, string expected)
    {
        using var test = new RunnerFixture();
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(mode), _ => { }, Token));
        Assert.Equal(expected, error.Code); Assert.False(File.Exists(test.Final)); Assert.False(File.Exists(test.Part));
        Assert.DoesNotContain(test.Artifacts.Root, error.ToString()); Assert.DoesNotContain("PRIVATE-NATIVE-SENTINEL", error.ToString());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Existing_part_or_final_are_preserved(bool final)
    {
        using var test = new RunnerFixture(); string target = final ? test.Final : test.Part; File.WriteAllText(target, "completed");
        Assert.Equal("worker.output.exists", (await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command("flood"), _ => { }, Token))).Code);
        Assert.Equal("completed", File.ReadAllText(target));
    }
    [Theory]
    [InlineData("stage")] [InlineData("flush")] [InlineData("move")]
    public async Task Injected_storage_failures_remove_only_request_staging(string point)
    {
        using var test = new RunnerFixture();
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner(publication: new FailPublication(point)).SynthesizeAsync(test.Command(), _ => { }, Token));
        Assert.Equal("worker.storage", error.Code);
        Assert.DoesNotContain("PRIVATE-DISK-DETAIL", error.ToString());
        Assert.False(File.Exists(test.Part)); Assert.False(File.Exists(test.Final));
    }
    [Fact]
    public async Task Tiny_positive_sampling_parameters_are_not_rounded_to_zero_in_native_argv()
    {
        using var test = new RunnerFixture(); using var selected = await test.Artifacts.Resolver().ResolveAsync("q8-f16-cpu", Token);
        var start = VoxCpmProcessRunner.BuildStartInfo(test.Command() with { Cfg = 1e-100, Temperature = 1e-99 }, test.Prompt, test.Part, selected);
        Assert.Equal("1E-100", start.ArgumentList[start.ArgumentList.IndexOf("--cfg") + 1]);
        Assert.Contains("--cpu", start.ArgumentList); Assert.Equal("0", start.ArgumentList[start.ArgumentList.IndexOf("--n-gpu-layers") + 1]);
    }
    [Fact]
    public async Task Locked_staging_cleanup_failure_keeps_registration_recoverable_and_is_private_safe()
    {
        using var test = new RunnerFixture(); using var publication = new LockedPublication();
        var error = await Assert.ThrowsAsync<WorkerException>(() => test.Runner(publication: publication).SynthesizeAsync(test.Command(), _ => { }, Token));
        Assert.Equal("worker.cleanup", error.Code); Assert.Equal("worker.cleanup", error.Message); Assert.True(File.Exists(test.Part));
    }
    private sealed class LockedPublication : IOutputPublication, IDisposable
    {
        private FileStream? file;
        public void Staged(string path) { file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); throw new IOException("PRIVATE-CLEANUP-DETAIL"); }
        public void BeforeFlush(string path) => throw new InvalidOperationException();
        public void Publishing(string part, string final) => throw new InvalidOperationException();
        public void Published(string final) => throw new InvalidOperationException();
        public void Dispose() => file?.Dispose();
    }
    [Fact]
    public async Task Output_flood_is_drained_with_bounded_classification_memory()
    {
        using var test = new RunnerFixture(); var result = await test.Runner().SynthesizeAsync(test.Command("flood"), _ => { }, Token);
        Assert.Equal(48, result.Length);
        var window = new NativeOutputClassifier(); for (int i = 0; i < 100; i++) window.Add(new string('x', 8192));
        Assert.True(window.RetainedCharacters <= 65536); window.Add("cudaMalloc failed: out of memory"); Assert.True(window.IsCudaOutOfMemory);
    }
    [Theory]
    [InlineData(0, 10, 2, 1, 42)] [InlineData(201, 10, 2, 1, 42)] [InlineData(70, 0, 2, 1, 42)]
    [InlineData(70, 101, 2, 1, 42)] [InlineData(70, 10, double.NaN, 1, 42)] [InlineData(70, 10, 2, 0, 42)]
    [InlineData(70, 10, 2, 1, 2147483648)]
    public async Task Invalid_parameters_are_rejected_before_process_or_storage(int steps, int timesteps, double cfg, double temperature, long seed)
    {
        using var test = new RunnerFixture(); var command = test.Command() with { Steps = steps, Timesteps = timesteps, Cfg = cfg, Temperature = temperature, Seed = seed };
        Assert.Equal("worker.arguments", (await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(command, _ => { }, Token))).Code);
        Assert.False(File.Exists(test.Part));
    }
    [Theory]
    [InlineData(" ")] [InlineData("x\0private")]
    public async Task Invalid_text_or_transcript_are_rejected(string text)
    {
        using var test = new RunnerFixture();
        Assert.Equal("worker.arguments", (await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(text), _ => { }, Token))).Code);
        Assert.Equal("worker.arguments", (await Assert.ThrowsAsync<WorkerException>(() => test.Runner().SynthesizeAsync(test.Command(transcript: text), _ => { }, Token))).Code);
    }
    private sealed class FailPublication(string point) : IOutputPublication
    {
        public void Staged(string path) { if (point == "stage") throw new IOException("PRIVATE-DISK-DETAIL"); }
        public void BeforeFlush(string path) { if (point == "flush") throw new IOException("PRIVATE-DISK-DETAIL"); }
        public void Publishing(string part, string final) { if (point == "move") throw new IOException("PRIVATE-DISK-DETAIL"); }
        public void Published(string final) { }
    }
    internal sealed class RunnerFixture : IDisposable
    {
        public ArtifactResolverTests.Artifacts Artifacts { get; } = new();
        public string Root { get; }
        public string Prompt => Path.Combine(Root, "voice", "эталон.wav");
        public string Final => Path.Combine(Root, "segments", "мо́ре.wav"); public string Part => Final + ".part";
        public RunnerFixture(int pathComponentLength = 90)
        {
            Root = Path.Combine(Artifacts.Root, "Проект с пробелами", new string('я', pathComponentLength));
            Artifacts.UseFakeExecutable(); Directory.CreateDirectory(Path.GetDirectoryName(Prompt)!); Directory.CreateDirectory(Path.GetDirectoryName(Final)!);
            using var output = File.Create(Prompt); using var w = new BinaryWriter(output);
            w.Write("RIFF"u8); w.Write(40); w.Write("WAVEfmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)1); w.Write(48000); w.Write(96000); w.Write((ushort)2); w.Write((ushort)16); w.Write("data"u8); w.Write(4); w.Write((short)120); w.Write((short)-120);
        }
        public SynthesizeSegmentCommand Command(string text = "valid", string transcript = "Точная речь") => new(text, "voice/эталон.wav", transcript, "segments/мо́ре.wav", "q8-f16-hybrid-16", 70, 10, 2, 1, 42);
        public VoxCpmProcessRunner Runner(TimeSpan? timeout = null, IOutputPublication? publication = null) => new(Root, Artifacts.Resolver(), timeout ?? TimeSpan.FromSeconds(10), publication);
        public void Dispose() => Artifacts.Dispose();
    }
}
