using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class StudioProjectServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_restart_replay_and_open_preserve_one_core_identity_and_monotonic_last_opened()
    {
        using var sandbox = new CatalogSandbox();
        var first = await sandbox.Service().CreateAsync(new("  Моя книга  "), "request-one", Token);
        Assert.True(first.Created);
        Assert.Equal("Моя книга", first.Snapshot.DisplayName);
        Assert.Matches("^[a-f0-9]{32}$", first.Snapshot.Id);
        var restarted = sandbox.Service();
        var replay = await restarted.CreateAsync(new("Моя книга"), "request-one", Token);
        Assert.False(replay.Created);
        Assert.Equal(first.Snapshot.Id, replay.Snapshot.Id);
        var before = Assert.Single(await restarted.ListAsync(Token));
        Assert.Equal(DateTimeOffset.MinValue, before.LastOpenedUtc);
        Assert.Equal(first.Snapshot.Id, (await restarted.GetAsync(first.Snapshot.Id, Token)).Id);
        Assert.Equal(before.LastOpenedUtc, Assert.Single(await restarted.ListAsync(Token)).LastOpenedUtc);
        await restarted.OpenAsync(first.Snapshot.Id, Token);
        var opened = Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.True(opened.LastOpenedUtc > before.LastOpenedUtc);
        await sandbox.Catalog().TouchOpenedAsync(first.Snapshot.Id, DateTimeOffset.MinValue, Token);
        Assert.Equal(opened.LastOpenedUtc, Assert.Single(await restarted.ListAsync(Token)).LastOpenedUtc);
        Assert.False(opened.HasRecording); Assert.False(opened.HasAnchor); Assert.False(opened.HasText);
        var state = await new ProjectService().ProbeAsync(sandbox.ProjectRoot(first.Snapshot.Id), Token);
        Assert.Equal(first.Snapshot.Id, state.Metadata.Id);
        Assert.Equal(state.Revision, opened.Revision);
        Assert.DoesNotContain(sandbox.Root, JsonSerializer.Serialize(first.Snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_same_key_across_instances_creates_exactly_one_project()
    {
        using var sandbox = new CatalogSandbox();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => sandbox.Service().CreateAsync(new("Книга"), "concurrent", Token), Token)));
        Assert.Single(results.Select(x => x.Snapshot.Id).Distinct());
        Assert.Single(results, x => x.Created);
        Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.Root, "projects")));
    }

    [Fact]
    public async Task Key_reuse_with_different_canonical_title_fails_and_unicode_is_not_normalized()
    {
        using var sandbox = new CatalogSandbox();
        await sandbox.Service().CreateAsync(new("é"), "key", Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new("e\u0301"), "key", Token));
        Assert.Equal("project.idempotency.conflict", error.Code);
        Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        Assert.Single(await sandbox.Service().ListAsync(Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\tКнига")]
    [InlineData("Книга\r\n")]
    [InlineData("Кни\0га")]
    public async Task Rejects_blank_or_raw_control_titles_without_creating_project(string title)
    {
        using var sandbox = new CatalogSandbox();
        Assert.Equal("project.title.invalid", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new(title), "key", Token))).Code);
        Assert.Empty(await sandbox.Service().ListAsync(Token));
    }

    [Fact]
    public async Task Title_length_is_120_utf16_units_after_outer_space_trim()
    {
        using var sandbox = new CatalogSandbox();
        Assert.Equal(new string('я', 120), (await sandbox.Service().CreateAsync(new(" " + new string('я', 120) + " "), "valid", Token)).Snapshot.DisplayName);
        Assert.Equal("project.title.invalid", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new(new string('я', 121)), "invalid", Token))).Code);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("12345678-1234-1234-1234-123456789abc")]
    public async Task Invalid_ids_are_path_free_errors(string id)
    {
        using var sandbox = new CatalogSandbox();
        var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().OpenAsync(id, Token));
        Assert.Equal("project.id.invalid", error.Code); Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public async Task Snapshot_maps_segment_hash_and_only_truthful_v1_detail_while_list_uses_core_state()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Old"), "key", Token);
        await using (var project = await new ProjectService().OpenAsync(sandbox.ProjectRoot(created.Snapshot.Id), Token))
        {
            var state = project.State; var now = DateTimeOffset.UtcNow;
            await project.SaveAsync(state with
            {
                Metadata = state.Metadata with { Title = "New" },
                Voice = new("voice", "voice-snapshot/synthetic.wav", "synthetic transcript", "wav", "transcript", "identity", true, now),
                Chapters = [new("chapter", 1, "chapter", "original", "normalized", "spoken", now)],
                Segments = [new("segment", "chapter", 1, "original", "normalized", "spoken", "content-hash", "fingerprint", null, null, 1, state.Metadata.Versions, state.Metadata.Parameters, "queued", false, now, now, null)]
            }, Token);
        }
        var snapshot = await sandbox.Service().GetAsync(created.Snapshot.Id, Token);
        Assert.Equal("New", snapshot.DisplayName); Assert.Equal(2, snapshot.Revision);
        Assert.Null(snapshot.VoiceRecording); Assert.Null(snapshot.VoiceAnchor); Assert.Null(snapshot.TextDocument);
        Assert.Equal("content-hash", Assert.Single(snapshot.Segments).ContentHash);
        var item = Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.True(item.HasRecording); Assert.True(item.HasAnchor); Assert.True(item.HasText);
        Assert.Equal("New", item.DisplayName); Assert.Equal(2, item.Revision);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("RelativePath", json); Assert.DoesNotContain("synthetic.wav", json);
    }
}
