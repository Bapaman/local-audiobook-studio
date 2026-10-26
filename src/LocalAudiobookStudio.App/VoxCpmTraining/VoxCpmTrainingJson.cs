using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal static class VoxCpmTrainingJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static VoxCpmTrainingExecutionException Invalid() => new("voxcpm_training.protocol.invalid");
    internal static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw Invalid(); }
    internal static JsonDocument Parse(byte[] bytes)
    {
        var value = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        try { Unique(value.RootElement); return value; } catch { value.Dispose(); throw; }
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
        Require(fields.Length == names.Length && fields.All(p => names.Contains(p.Name, StringComparer.Ordinal)) && fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == names.Length);
    }
    internal static string Text(JsonElement value) { Require(value.ValueKind == JsonValueKind.String); return value.GetString()!; }
    internal static long Integer(JsonElement value, long minimum, long maximum)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.GetRawText().All(c => c is >= '0' and <= '9') && value.TryGetInt64(out long result) && result >= minimum && result <= maximum);
        return value.GetInt64();
    }
    internal static string Hash(JsonElement value) { string text = Text(value); Require(IsHash(text)); return text; }
    internal static bool IsHash(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static byte[] Canonical(JsonElement value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) Write(writer, value);
        return bytes.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(field.Name); Write(writer, field.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    internal static async Task<byte[]> Bytes(FileStream input, CancellationToken token)
    {
        byte[] bytes = new byte[checked((int)input.Length)]; input.Position = 0; await input.ReadExactlyAsync(bytes, token); input.Position = 0; return bytes;
    }
}
