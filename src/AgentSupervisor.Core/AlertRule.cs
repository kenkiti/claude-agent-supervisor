namespace AgentSupervisor.Core;

public sealed record AlertRule(
    string RuleId,
    bool Enabled,
    string Severity,
    string Condition,
    int CooldownSeconds,
    int RepeatIntervalSeconds,
    IReadOnlyList<string> Channels);

public sealed record AlertCandidate(
    string NotificationType,
    string Severity,
    string RuntimeId,
    string? SessionId,
    long StateVersion,
    string PayloadJson,
    IReadOnlyList<string> Channels);
