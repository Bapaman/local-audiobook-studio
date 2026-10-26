using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class StudioTextServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static StudioTextService Service(CatalogSandbox box) => new(box.Service());
    private static async Task<ProjectSnapshotDto> Create(CatalogSandbox box) => (await box.Service().CreateAsync(new("Synthetic"), "key", Token)).Snapshot;
    private const string Original = "  Ёжик  идёт.\r\nОн чита\u0301ет.  ";

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public async Task File_provenance_exact_bytes_and_order_survive_reopen(string encoding)
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        Encoding codec = encoding switch { "utf-16le" => new UnicodeEncoding(false, true, true), "utf-16be" => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(false, true) };
        byte[] bytes = [.. codec.GetPreamble(), .. codec.GetBytes(Original)]; using var stream = new MemoryStream(bytes);
        var imported = await Service(box).ImportFileAsync(created.Id, created.Revision, "C:\\synthetic\\книга.txt", stream, Token);
        var document = Assert.IsType<TextDocumentDto>(imported.TextDocument);
        Assert.Equal("File", document.SourceKind); Assert.Equal("книга.txt", document.DisplayName); Assert.Equal(encoding, document.Encoding);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.Sha256); Assert.Equal(bytes.Length, document.ByteCount);
        Assert.Equal(Original, document.OriginalText); Assert.Equal("Ёжик идёт.\nОн чита\u0301ет.", document.NormalizedText);
        Assert.Equal(new[] { "Ёжик идёт.", "Он чита\u0301ет." }, imported.Segments.Select(x => x.SpokenText));
        Assert.All(imported.Segments, x => Assert.Equal("draft", x.Status));
        Assert.Equal(new[] { 0, 1 }, imported.Segments.Select(x => x.Order));
        var reopened = await box.Service().OpenAsync(created.Id, Token);
        Assert.Equal(document, reopened.TextDocument); Assert.Equal(imported.Segments, reopened.Segments);
        Assert.DoesNotContain("C:\\synthetic", JsonSerializer.Serialize(reopened)); Assert.DoesNotContain(box.Root, JsonSerializer.Serialize(reopened));
    }

    [Fact]
    public async Task Paste_is_exact_and_spoken_edit_changes_canonical_hash_clearing_generation()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("  Синтетика  ", Original), Token);
        Assert.Equal("Pasted", imported.TextDocument!.SourceKind); Assert.Equal("Синтетика", imported.TextDocument.DisplayName);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Original))), imported.TextDocument.Sha256);
        await using (var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token))
        {
            await project.SaveAsync(project.State with { Segments = project.State.Segments.Select(x => x with { GenerationFingerprint = "old", OutputHash = "old", OutputRelativePath = "output/synthetic.wav", Status = "done" }).ToArray() }, Token);
        }
        var before = await box.Service().GetAsync(created.Id, Token);
        const string spoken = "Ёжик идё\u0301т.";
        var edited = await Service(box).UpdateSpokenTextAsync(created.Id, before.Segments[0].Id, before.Revision, new(spoken), Token);
        Assert.Equal(imported.TextDocument, edited.TextDocument); Assert.Equal(spoken, edited.Segments[0].SpokenText);
        Assert.Equal(before.Segments[0].OriginalText, edited.Segments[0].OriginalText); Assert.Equal(before.Segments[0].NormalizedText, edited.Segments[0].NormalizedText);
        Assert.NotEqual(before.Segments[0].ContentHash, edited.Segments[0].ContentHash); Assert.Equal(before.Segments[1], edited.Segments[1]);
        // First piece ends a paragraph, with the next sentence as context. This independent public framing verifies persisted contexts.
        Assert.Equal(SemanticSegmenter.ComputeContentHash("Ёжик идёт.", spoken, null, "Он чита\u0301ет.", RussianTextFrontend.Version, SemanticSegmenter.Version, SegmentBoundary.Paragraph), edited.Segments[0].ContentHash);
        var state = await new ProjectService().ProbeAsync(box.ProjectRoot(created.Id), Token); var segment = state.Segments[0];
        Assert.Equal("", segment.GenerationFingerprint); Assert.Null(segment.OutputHash); Assert.Null(segment.OutputRelativePath); Assert.Equal("draft", segment.Status);
        Assert.Equal(imported.TextDocument, (await box.Service().GetAsync(created.Id, Token)).TextDocument);
    }

    [Theory]
    [InlineData("", "text.empty")]
    [InlineData(" \u0301а.", "text.grapheme_boundary")]
    [InlineData("а\0б", "text.binary")]
    public async Task Invalid_paste_or_spoken_input_leaves_revision_unchanged(string text, string code)
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        Assert.Equal(code, (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", text), Token))).Code);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", "Ёжик."), Token);
        Assert.Equal(code, (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, imported.Revision, new(text), Token))).Code);
        Assert.Equal(imported.Revision, (await box.Service().GetAsync(created.Id, Token)).Revision);
    }

    [Fact]
    public async Task Paste_limit_long_token_and_spoken_limit_are_rejected()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        Assert.Equal("text.too_large", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", new string('а', 8_000_001)), Token))).Code);
        Assert.Equal("text.token_too_long", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", new string('я', 321)), Token))).Code);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", "Ёжик."), Token);
        Assert.Equal("text.too_large", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, imported.Revision, new(new string('я', 321)), Token))).Code);
    }

    [Fact]
    public async Task Unpaired_surrogate_is_rejected_for_import_and_edit()
    {
        using var box = new CatalogSandbox(); var created = await Create(box); string invalid = "а" + (char)0xd800;
        Assert.Equal("text.encoding.invalid", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", invalid), Token))).Code);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", "Ёжик."), Token);
        Assert.Equal("text.encoding.invalid", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, imported.Revision, new(invalid), Token))).Code);
    }

    [Fact]
    public async Task Revision_is_refreshed_after_streaming_before_acceptance()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        using var stream = new ReadHookStream("Ёжик."u8.ToArray(), () => CatalogSandbox.Sql(box.ProjectRoot(created.Id), "UPDATE project SET revision=revision+1"));
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportFileAsync(created.Id, created.Revision, "synthetic.txt", stream, Token))).Code);
        var snapshot = await box.Service().GetAsync(created.Id, Token); Assert.Null(snapshot.TextDocument); Assert.Empty(snapshot.Segments);
        Assert.Equal(created.Revision + 1, snapshot.Revision);
    }

    [Fact]
    public async Task Over_limit_file_is_rejected_before_segmentation()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        using var stream = new MemoryStream(new byte[TextImportService.MaximumFileBytes + 1]);
        Assert.Equal("text.too_large", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportFileAsync(created.Id, created.Revision, "synthetic.txt", stream, Token))).Code);
        Assert.Empty((await box.Service().GetAsync(created.Id, Token)).Segments);
    }

    [Theory]
    [InlineData("610062", "text.binary")]
    [InlineData("FF", "text.encoding.invalid")]
    [InlineData("FFFE000041000000", "text.encoding.unsupported")]
    [InlineData("FFFE41", "text.encoding.invalid")]
    public async Task File_decoder_errors_are_fixed_codes_with_no_acceptance(string hex, string code)
    {
        using var box = new CatalogSandbox(); var created = await Create(box); using var stream = new MemoryStream(Convert.FromHexString(hex));
        var error = await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportFileAsync(created.Id, created.Revision, "synthetic.txt", stream, Token));
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message); Assert.Null(error.InnerException);
        Assert.Null((await box.Service().GetAsync(created.Id, Token)).TextDocument);
    }

    [Fact]
    public async Task Stale_revision_and_duplicate_are_checked_before_reading_stream()
    {
        using var box = new CatalogSandbox(); var created = await Create(box); using var stream = new MemoryStream("Ёжик."u8.ToArray());
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportFileAsync(created.Id, created.Revision - 1, "synthetic.txt", stream, Token))).Code);
        Assert.Equal(0, stream.Position);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", "Ёжик."), Token);
        Assert.Equal("text.exists", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportFileAsync(created.Id, imported.Revision, "second.txt", stream, Token))).Code);
        Assert.Equal(0, stream.Position);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, created.Revision, new("Новое."), Token))).Code);
        Assert.Equal("text.segment.missing", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, "missing", imported.Revision, new("Новое."), Token))).Code);
    }

    [Fact]
    public async Task Legacy_segment_without_nullable_metadata_requires_reimport()
    {
        using var box = new CatalogSandbox(); var created = await Create(box); var now = DateTimeOffset.UtcNow;
        await using (var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token))
        {
            var state = project.State;
            await project.SaveAsync(state with { Chapters = [new("chapter", 0, "Synthetic", "Ёжик.", "Ёжик.", "Ёжик.", now)], Segments = [new("legacy", "chapter", 0, "Ёжик.", "Ёжик.", "Ёжик.", "old", "", null, null, 0, state.Metadata.Versions, state.Metadata.Parameters, "draft", false, now, now, null)] }, Token);
        }
        CatalogSandbox.Sql(box.ProjectRoot(created.Id), "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; DROP TABLE voice_recording; PRAGMA user_version=1");
        var legacy = await box.Service().GetAsync(created.Id, Token); Assert.Null(legacy.TextDocument);
        Assert.Equal("text.reimport_required", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, "legacy", legacy.Revision, new("Новое."), Token))).Code);
        Assert.Equal(legacy.Segments, (await box.Service().GetAsync(created.Id, Token)).Segments);
    }

    [Fact]
    public async Task Core_direct_or_stale_save_cannot_replace_accepted_original_or_provenance()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", Original), Token);
        await using (var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token))
        await using (var stale = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token))
        {
            Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => stale.SaveAsync(stale.State with { Chapters = [], Segments = [] }, Token))).Code);
            var chapter = project.State.Chapters[0];
            foreach (var replacement in new[] { chapter with { OriginalText = "changed" }, chapter with { NormalizedText = "changed" }, chapter with { Provenance = chapter.Provenance! with { SourceHash = new string('0', 64) } } })
                Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(project.State with { Chapters = [replacement] }, Token))).Code);
            await project.SaveAsync(project.State with { Metadata = project.State.Metadata with { Author = "Synthetic" } }, Token);
            Assert.Equal("project.conflict", (await Assert.ThrowsAsync<ProjectException>(() => stale.SaveAsync(stale.State, Token))).Code);
        }
        Assert.Equal(imported.TextDocument, (await box.Service().GetAsync(created.Id, Token)).TextDocument);
    }

    [Fact]
    public async Task Word_boundary_zero_and_neighbor_contexts_survive_reopen_and_allow_exact_spoken_edit()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        string original = "До.\n" + string.Join(' ', Enumerable.Repeat("слово", 90)) + ".\nПосле.";
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", original), Token);
        var state = await new ProjectService().ProbeAsync(box.ProjectRoot(created.Id), Token);
        var word = Assert.Single(state.Segments, x => x.BoundaryAfter == SegmentBoundary.Word);
        Assert.Equal(SemanticSegmenter.ComputeSentenceContextHash("До."), word.PreviousSentenceContextHash);
        Assert.Equal(SemanticSegmenter.ComputeSentenceContextHash("После."), word.NextSentenceContextHash);
        const string spoken = "  Сло\u0301во  изменено.  ";
        var edited = await Service(box).UpdateSpokenTextAsync(created.Id, word.Id, imported.Revision, new(spoken), Token);
        var actual = Assert.Single(edited.Segments, x => x.Id == word.Id); Assert.Equal(spoken, actual.SpokenText);
        Assert.Equal(SemanticSegmenter.ComputeContentHash(word.NormalizedText, spoken, "До.", "После.", RussianTextFrontend.Version, SemanticSegmenter.Version, SegmentBoundary.Word), actual.ContentHash);
    }

    [Fact]
    public async Task Core_cannot_rewrite_imported_segment_original_normalized_or_hash_contexts()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", Original), Token);
        await using var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token); var segment = project.State.Segments[0];
        foreach (var replacement in new[] { segment with { OriginalText = "changed" }, segment with { NormalizedText = "changed" }, segment with { NextSentenceContextHash = null }, segment with { BoundaryAfter = SegmentBoundary.Word }, segment with { Order = 99 }, segment with { Versions = segment.Versions with { TextFrontend = "changed" } } })
            Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(project.State with { Segments = project.State.Segments.Select(x => x.Id == segment.Id ? replacement : x).ToArray() }, Token))).Code);
        Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(project.State with { Segments = project.State.Segments.Skip(1).ToArray() }, Token))).Code);
    }

    [Fact]
    public async Task Missing_boundary_is_not_treated_as_valid_word_zero()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var imported = await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", "Ёжик."), Token);
        CatalogSandbox.Sql(box.ProjectRoot(created.Id), "UPDATE segments SET payload=json_remove(payload,'$.BoundaryAfter')");
        Assert.Equal("text.reimport_required", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, imported.Revision, new("Новое."), Token))).Code);
        Assert.Equal(imported.Segments, (await box.Service().GetAsync(created.Id, Token)).Segments);
    }

    [Fact]
    public async Task Text_import_edit_and_owner_recovery_preserve_accepted_synthetic_voice_source_and_anchor()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var voices = new StudioVoiceService(box.Service(), new VoiceRecordingService(new(48000, 1440000)));
        using var wav = SyntheticWav();
        var recording = await voices.UploadRecordingAsync(created.Id, created.Revision, new("synthetic.wav", true), wav, Token);
        var voice = await voices.ConfirmAnchorAsync(created.Id, recording.Revision, new(1000, 11000, "Синтетический якорь.", true), Token);
        var imported = await Service(box).ImportPastedAsync(created.Id, voice.Revision, new("Synthetic", Original), Token);
        var edited = await Service(box).UpdateSpokenTextAsync(created.Id, imported.Segments[0].Id, imported.Revision, new("Ёжик читает."), Token);
        string source;
        await using (var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token))
        {
            source = project.ResolveOwnedPath(project.State.VoiceRecording!.WavRelativePath);
            await project.RegisterPendingArtifactAsync(project.State.VoiceRecording.WavRelativePath + ".part", project.State.VoiceRecording.WavRelativePath, Token);
            await project.SaveAsync(project.State with { Segments = project.State.Segments.Select(x => x.Id == edited.Segments[0].Id ? x with { Status = "running" } : x).ToArray() }, Token);
        }
        var unrecovered = await box.Service().OpenAsync(created.Id, Token);
        Assert.Equal("running", unrecovered.Segments[0].Status);
        // Recovery is an explicit owned maintenance operation, never browser Open.
        await using (var owner = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token, retainSidecars: true)) { }
        var recovered = await box.Service().OpenAsync(created.Id, Token);
        Assert.Equal(voice.VoiceRecording, recovered.VoiceRecording); Assert.Equal(voice.VoiceAnchor, recovered.VoiceAnchor);
        Assert.Equal(edited.TextDocument, recovered.TextDocument); Assert.Equal("Ёжик читает.", recovered.Segments[0].SpokenText);
        Assert.Equal("queued", recovered.Segments[0].Status); Assert.Equal(edited.Segments.Select(x => x.Id), recovered.Segments.Select(x => x.Id));
        Assert.Equal(voice.VoiceRecording!.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, Token))));
    }

    [Theory]
    [InlineData("", "text.display_name.invalid")]
    [InlineData("bad\nname", "text.display_name.invalid")]
    [InlineData("folder/", "text.display_name.invalid")]
    public async Task Labels_are_validated_without_exposing_paths_or_text(string label, string code)
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var error = await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, created.Revision, new(label, Original), Token));
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("chapters", "$.Provenance.SourceHash", "'bad'")]
    [InlineData("chapters", "$.Provenance.SourceEncoding", "'unsupported'")]
    [InlineData("chapters", "$.Provenance.DisplayName", "'folder/book.txt'")]
    [InlineData("segments", "$.BoundaryAfter", "99")]
    [InlineData("segments", "$.NextSentenceContextHash", "'bad'")]
    public async Task Malformed_new_metadata_is_rejected_before_guarded_mapping(string table, string jsonPath, string literal)
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        await Service(box).ImportPastedAsync(created.Id, created.Revision, new("Synthetic", Original), Token);
        CatalogSandbox.Sql(box.ProjectRoot(created.Id), $"UPDATE {table} SET payload=json_set(payload,'{jsonPath}',{literal})");
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(() => new ProjectService().ProbeAsync(box.ProjectRoot(created.Id), Token))).Code);
        Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => box.Service().GetAsync(created.Id, Token))).Code);
    }

    [Fact]
    public async Task Competing_live_services_accept_one_import_and_preserve_order()
    {
        using var box = new CatalogSandbox(); var created = await Create(box);
        var first = Service(box).ImportPastedAsync(created.Id, created.Revision, new("First", Original), Token);
        var second = Service(box).ImportPastedAsync(created.Id, created.Revision, new("Second", "Другой текст."), Token);
        var outcomes = await Task.WhenAll(Observe(first), Observe(second));
        Assert.Single(outcomes, x => x == "accepted"); Assert.Single(outcomes, x => x == "project.conflict");
        var snapshot = await box.Service().OpenAsync(created.Id, Token); Assert.NotNull(snapshot.TextDocument);
        Assert.Equal("text.exists", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ImportPastedAsync(created.Id, snapshot.Revision, new("Third", "Ещё."), Token))).Code);
    }

    private static async Task<string> Observe(Task<ProjectSnapshotDto> task)
    {
        try { await task; return "accepted"; } catch (StudioException e) { return e.Code; }
    }

    private sealed class ReadHookStream(byte[] bytes, Action firstRead) : MemoryStream(bytes)
    {
        private bool called;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!called) { called = true; firstRead(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static MemoryStream SyntheticWav()
    {
        var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(2880036u); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(48000u); writer.Write(96000u); writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(2880000u);
            for (int i = 0; i < 1440000; i++) writer.Write((short)1000);
        }
        stream.Position = 0; return stream;
    }
}
