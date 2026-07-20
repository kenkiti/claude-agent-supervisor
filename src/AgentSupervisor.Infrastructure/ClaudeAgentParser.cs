using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public static class ClaudeAgentParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new UnixMillisecondsDateTimeOffsetConverter() } };

    public static IReadOnlyList<ClaudeAgent> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // `claude agents --json --all` itself returns a bare JSON array. Some fixtures
        // (this repo's Phase 0 captures) wrap that array in {"result": [...]} metadata,
        // so accept both shapes -- but JsonElement.TryGetProperty throws instead of
        // returning false when called on a non-Object element, so ValueKind must be
        // checked first.
        var result = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var resultElement)
            ? resultElement
            : root;
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<ClaudeAgent>();
        return result.EnumerateArray().Select(x => JsonSerializer.Deserialize<ClaudeAgent>(x.GetRawText(), Options)).Where(x => x is not null).Cast<ClaudeAgent>().ToArray();
    }
}

internal sealed class UnixMillisecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value)) return DateTimeOffset.FromUnixTimeMilliseconds(value);
        if (reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(), out var parsed)) return parsed;
        throw new JsonException("startedAt must be an ISO timestamp or Unix milliseconds.");
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value);
    }
}
