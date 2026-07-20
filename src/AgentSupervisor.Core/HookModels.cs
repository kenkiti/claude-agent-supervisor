namespace AgentSupervisor.Core;

public sealed record HookEvent(string? RuntimeId, string? SessionId, string? SourceEventId, string? EventName, DateTimeOffset? OccurredAt, System.Text.Json.JsonElement Payload);
public static class HookPayloadTranslator
{
    public static HookEvent Translate(System.Text.Json.JsonElement raw, string runtimeId)
    {
        var name = raw.TryGetProperty("hook_event_name", out var n) ? n.GetString() : null;
        var session = raw.TryGetProperty("session_id", out var s) ? s.GetString() : null;
        var source = raw.TryGetProperty("tool_use_id", out var t) ? t.GetString() : null;
        source ??= Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw.GetRawText())))[..16];
        return new HookEvent(runtimeId, session, source, name, DateTimeOffset.UtcNow, raw.Clone());
    }
}
