namespace AgentSupervisor.Core;

public interface IClaudeRuntime
{
    string RuntimeId { get; }
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken cancellationToken = default);
    Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken cancellationToken = default);
    Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken cancellationToken = default);
    Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken cancellationToken = default);
    Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken cancellationToken = default);
    Task<RuntimeCommandResult> GetGitStatusAsync(string cwd, CancellationToken cancellationToken = default) => Task.FromResult(new RuntimeCommandResult("git status", -1, "", "not supported"));
    Task<StartedProcessHandle> ResumeBatchAsync(string cwd, string sessionId, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken cancellationToken = default) => StartBatchAsync(cwd, prompt, maxTurns, maxBudgetUsd, cancellationToken);
    Task<StartedProcessHandle> RespawnBackgroundAsync(string cwd, string? claudeJobId, string prompt, CancellationToken cancellationToken = default) => StartBackgroundAsync(cwd, prompt, cancellationToken);
    Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAsync(string jobId, CancellationToken cancellationToken = default);
    string GetStdoutExcerpt(string jobId);
    string GetStderrExcerpt(string jobId);
}
