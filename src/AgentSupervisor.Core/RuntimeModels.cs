namespace AgentSupervisor.Core;

public sealed record ClaudeAgent(
    int? Pid,
    string? Cwd,
    string? Kind,
    DateTimeOffset? StartedAt,
    string? SessionId,
    string? Name,
    string? Status,
    string? State = null,
    string? WaitingFor = null);

public sealed record RuntimeCommandResult(string Command, int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record RuntimeSnapshot(
    string RuntimeId,
    IReadOnlyList<ClaudeAgent> Agents,
    RuntimeCommandResult DaemonStatus,
    RuntimeCommandResult AuthStatus,
    DateTimeOffset CapturedAt,
    string? Error = null);

public sealed record ObserveOnlySession(
    string SessionId,
    string RuntimeId,
    string LastEventName,
    DateTimeOffset OccurredAt);

public sealed record NormalizedSession(
    string Id,
    string? Project,
    string Runtime,
    string LifecycleState,
    string ActivityState,
    string AttentionState,
    string HealthState,
    DateTimeOffset UpdatedAt,
    string Source,
    string Confidence);

public sealed record StartedProcessHandle(string JobId, int ProcessId);
public sealed record ProjectRecord(string Id, string RuntimeId, string Cwd, int MaxParallel, string DefaultMode);
public sealed record RuntimeRecord(string Id, string Type, string? Distribution, string ClaudeCommand = "claude");
public sealed record PendingQuestionRecord(string Id, string RuntimeId, string? SessionId, string QuestionsJson, string Status, string? AnswersJson, DateTimeOffset CreatedAt, DateTimeOffset? AnsweredAt);
public sealed record TaskRecord(string Id, string ProjectId, string Prompt, string Mode, string Status, int MaxTurns, decimal MaxBudgetUsd, int TimeoutSeconds, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MaxRetries = 3, bool ManualStop = false);
public sealed record TaskAttemptRecord(string Id, string TaskId, int AttemptNumber, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, int? ExitCode, string Status, string? StdoutPath, string? StdoutExcerpt, string? StderrPath, string? StderrExcerpt, decimal? CostUsd, int? TurnsUsed, string? Error, string? FailureCategory = null, string? Fingerprint = null, string? GitStatus = null, string? ClaudeSessionId = null);
public sealed record StreamJsonEvent(string Type, decimal? CostUsd = null, int? TurnsUsed = null, string? Result = null, string? Error = null);

public sealed record RecoveryDecision(string Action, string Reason);

public static class FailureClassifier
{
    private static readonly string[] RetrySignals = ["rate limit", "rate_limit", "overloaded", "5xx", "500", "502", "503", "529", "network", "econnreset", "econnrefused", "timed out", "timeout", "daemon"];
    private static readonly string[] ManualSignals = ["authentication", "401", "unauthorized", "billing", "payment", "permission denied", "eacces", "test failed", "tests failed", "assertion", "compilation error", "build failed", "merge conflict", "conflict marker"];
    public static string Classify(int? exitCode, string? stdout, string? stderr)
    {
        if (exitCode is 0) return "none";
        if (exitCode is null) return "retry";
        var text = (stdout ?? "") + "\n" + (stderr ?? "");
        if (ManualSignals.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase))) return "manual";
        if (RetrySignals.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase))) return "retry";
        return "manual";
    }
    public static string Fingerprint(string category, int? exitCode, string? stderr)
    {
        var line = (stderr ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, "\\d+", "#");
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{category}|{exitCode}|{line}"));
        return Convert.ToHexString(bytes)[..16];
    }
}

public static class RecoveryPolicy
{
    public static TimeSpan Backoff(int attemptNumber) => TimeSpan.FromSeconds(Math.Min(1800, 30 * Math.Pow(2, Math.Max(0, attemptNumber - 1))));
    public static RecoveryDecision Decide(string category, int attemptNumber, int maxRetries, bool manualStopRequested, bool sameFingerprintAsPreviousAttempt, bool dailyBudgetExceeded)
    {
        if (dailyBudgetExceeded) return new("budget-exceeded", "daily cost budget exceeded");
        if (manualStopRequested) return new("manual", "manual stop requested");
        if (category == "manual") return new("manual", "failure requires manual handling");
        if (sameFingerprintAsPreviousAttempt) return new("exhausted", "same failure fingerprint repeated");
        if (attemptNumber >= maxRetries) return new("exhausted", "maximum retries reached");
        return new("retry", "transient failure is retryable");
    }
}

public static class StreamJsonParser
{
    public static string? FindSessionId(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        foreach (var line in stdout.Split('\n')) try { using var doc = System.Text.Json.JsonDocument.Parse(line); if (doc.RootElement.TryGetProperty("session_id", out var id)) return id.GetString(); } catch (System.Text.Json.JsonException) { }
        return null;
    }
    public static (decimal? CostUsd, int? TurnsUsed) FindResult(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return (null, null);
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result")
                {
                    var cost = doc.RootElement.TryGetProperty("total_cost_usd", out var c) ? c.GetDecimal() : (decimal?)null;
                    var turns = doc.RootElement.TryGetProperty("num_turns", out var n) ? n.GetInt32() : (int?)null;
                    return (cost, turns);
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return (null, null);
    }
}
