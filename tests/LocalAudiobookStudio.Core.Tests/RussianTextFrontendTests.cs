using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public class RussianTextFrontendTests
{
    [Fact]
    public void Normalization_changes_only_derived_forms_preserving_author_content()
    {
        const string original = " \tЕ\u0308ж  еще\u0308\tзна́ет 12.10.2026, 3.14 и т. д. \r\n\r— «Ёлка-ёлка!»  \n  Конец.\t";
        const string expected = "Ёж ещё зна́ет 12.10.2026, 3.14 и т. д.\n\n— «Ёлка-ёлка!»\nКонец.";
        var imported = new TextImportService().ImportPastedText(original);
        var normalized = new RussianTextFrontend().Normalize(imported);
        Assert.Same(imported, normalized.Imported);
        Assert.Equal(original, normalized.OriginalText);
        Assert.Equal(expected, normalized.Normalized);
        Assert.Equal(expected, normalized.Spoken);
        Assert.Equal("ru-preserve-v1", normalized.FrontendVersion);
        Assert.Equal(original, imported.OriginalText);
    }

    [Theory]
    [InlineData("ЁЖ ёж зна́ет", "ЁЖ ёж зна́ет")]
    [InlineData(" А\u00a0\u2003Б\t В \r Г ", "А Б В\nГ")]
    [InlineData("\n\n А \n\n\n Б \n", "\n\nА\n\n\nБ\n")]
    [InlineData("— Да!\n— Нет...\n(«Что?»)", "— Да!\n— Нет...\n(«Что?»)")]
    public void Normalization_is_idempotent_and_preserves_newline_boundaries(string original, string expected)
    {
        var importer = new TextImportService(); var frontend = new RussianTextFrontend();
        var first = frontend.Normalize(importer.ImportPastedText(original));
        Assert.Equal(expected, first.Normalized);
        Assert.Equal(expected, frontend.Normalize(importer.ImportPastedText(first.Normalized)).Normalized);
    }

    [Theory]
    [InlineData("Г\u0301", "Г\u0301")]
    [InlineData("а\u0301", "а\u0301")]
    [InlineData("a\u0301", "a\u0301")]
    [InlineData("е\u0308\u0301", "ё\u0301")]
    [InlineData("a\u0301\u0308", "a\u0301\u0308")]
    [InlineData("a\u0308\u0301", "ä\u0301")]
    public void Explicit_acute_stress_marks_survive_composition_and_neighboring_combining_marks(string original, string expected)
    {
        var importer = new TextImportService(); var frontend = new RussianTextFrontend();
        var normalized = frontend.Normalize(importer.ImportPastedText(original));
        Assert.Equal(expected, normalized.Normalized);
        Assert.Contains('\u0301', normalized.Spoken);
        Assert.Equal(original, normalized.OriginalText);
        Assert.Equal(expected, frontend.Normalize(importer.ImportPastedText(normalized.Normalized)).Normalized);
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void Unicode_line_and_paragraph_separators_become_preserved_line_boundaries(char separator)
    {
        string original = "  Ёж. " + separator + " — Да! ";
        var normalized = new RussianTextFrontend().Normalize(new TextImportService().ImportPastedText(original));
        Assert.Equal("Ёж.\n— Да!", normalized.Normalized);
        Assert.Equal(original, normalized.OriginalText);
    }
}
