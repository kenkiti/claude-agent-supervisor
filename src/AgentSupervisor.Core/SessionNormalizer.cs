namespace AgentSupervisor.Core;

public static class SessionNormalizer
{
    public static NormalizedSession Normalize(ClaudeAgent agent, string runtimeId, DateTimeOffset updatedAt)
    {
        var activity = agent.Status?.ToLowerInvariant() switch
        {
            "busy" or "working" or "running" => "active",
            "waiting" or "idle" => "idle",
            _ => "unknown"
        };
        var attention = agent.WaitingFor is not null ? "required" : "none";
        var lifecycle = agent.State?.ToLowerInvariant() switch
        {
            "done" => "finished",
            "stopped" => "stopped",
            _ => "running"
        };
        return new(agent.SessionId ?? agent.Pid?.ToString() ?? Guid.NewGuid().ToString("N"), agent.Cwd, runtimeId,
            lifecycle, activity, attention, "unknown", updatedAt, "claude-agent-view", "observed");
    }
}
