using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.TtsCorpus;

/// <summary>Strict IPC JSON: exact property sets, no duplicates, and no integer aliases.</summary>
internal static class TtsCorpusJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static TtsCorpusExecutionException Invalid() => new("tts_corpus.protocol.invalid");
    internal static void Require(bool condition) { if (!condition) throw Invalid(); }
    internal static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { Require(names.Add(field.Name)); Unique(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var fields = value.EnumerateObject().ToArray();
        Require(fields.Length == names.Length && fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == names.Length
            && fields.All(p => names.Contains(p.Name, StringComparer.Ordinal)));
    }
    internal static string Text(JsonElement value) { Require(value.ValueKind == JsonValueKind.String); return value.GetString()!; }
    internal static long Integer(JsonElement value, long minimum, long maximum)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.GetRawText().All(c => c is >= '0' and <= '9')
            && value.TryGetInt64(out long parsed) && parsed >= minimum && parsed <= maximum);
        return value.GetInt64();
    }
    internal static bool Boolean(JsonElement value) { Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False); return value.GetBoolean(); }
    internal static string Hash(JsonElement value) { string text = Text(value); Require(IsHash(text)); return text; }
    internal static bool IsHash(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsId(string text) => text.Length == 32 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    // Sort object keys while preserving numeric token spelling. 0 and 0.0 are
    // legitimate word times but may not silently substitute duplicated values.
    internal static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(p => Equal(p.First, p.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            _ => left.GetRawText() == right.GetRawText()
        };
    }
    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        var l = left.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        var r = right.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        return l.Length == r.Length && l.Zip(r).All(p => p.First.Name == p.Second.Name && Equal(p.First.Value, p.Second.Value));
    }
}
