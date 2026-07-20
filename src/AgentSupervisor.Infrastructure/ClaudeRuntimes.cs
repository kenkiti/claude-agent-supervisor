using System.Text.Json;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public abstract class ClaudeRuntimeBase : IClaudeRuntime
{
    protected readonly IProcessRunner Runner;
    protected abstract string Executable { get; }
    protected abstract IReadOnlyList<string> PrefixArguments { get; }
    public abstract string RuntimeId { get; }
    protected ClaudeRuntimeBase(IProcessRunner runner) => Runner = runner;
    protected IStreamingProcessRunner Streaming => Runner as IStreamingProcessRunner ?? throw new InvalidOperationException("Streaming process runner is required.");
    protected Task<RuntimeProbeResult> RunAsync(IReadOnlyList<string> args, CancellationToken token) => Runner.RunAsync(Executable, PrefixArguments.Concat(args).ToArray(), token);
    protected virtual (IReadOnlyList<string> Args, string Cwd) BuildInvocation(IReadOnlyList<string> args, string cwd) => (PrefixArguments.Concat(args).ToArray(), cwd);
    public async Task<bool> HealthCheckAsync(CancellationToken token = default) => (await RunAsync(new[] { "--version" }, token)).Succeeded;
    public async Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken token = default)
    {
        var result = await RunAsync(includeAll ? new[] { "agents", "--json", "--all" } : new[] { "agents", "--json" }, token);
        if (!result.Succeeded) return Array.Empty<ClaudeAgent>();
        try { return ClaudeAgentParser.Parse(result.Output); } catch (JsonException) { return Array.Empty<ClaudeAgent>(); }
    }
    public Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken token = default) => RunCommandAsync(new[] { "daemon", "status" }, token);
    public Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken token = default) => RunCommandAsync(new[] { "auth", "status" }, token);
    private async Task<RuntimeCommandResult> RunCommandAsync(IReadOnlyList<string> args, CancellationToken token) { var r = await RunAsync(args, token); return new(r.Command, r.ExitCode, r.Output, r.Error); }
    public Task<RuntimeCommandResult> GetGitStatusAsync(string cwd, CancellationToken token = default) => RunCommandAtAsync(new[] { "git", "status", "--porcelain" }, cwd, token);
    private async Task<RuntimeCommandResult> RunCommandAtAsync(IReadOnlyList<string> args, string cwd, CancellationToken token) { var (a, c) = BuildInvocation(args, cwd); var r = await Runner.RunAsync(Executable, a, token, c); return new(r.Command, r.ExitCode, r.Output, r.Error); }
    public Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken ct = default) { var (a, c) = BuildInvocation(new[] { "--bg", prompt }, cwd); return Streaming.StartAsync(Executable, a, c, ct); }
    public Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) { var (a, c) = BuildInvocation(new[] { "-p", prompt, "--output-format", "stream-json", "--verbose", "--max-turns", maxTurns.ToString(), "--max-budget-usd", maxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture), "--permission-mode", "dontAsk" }, cwd); return Streaming.StartAsync(Executable, a, c, ct); }
    public Task<StartedProcessHandle> ResumeBatchAsync(string cwd, string sessionId, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) { var (a, c) = BuildInvocation(new[] { "-p", prompt, "--resume", sessionId, "--output-format", "stream-json", "--verbose", "--max-turns", maxTurns.ToString(), "--max-budget-usd", maxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture), "--permission-mode", "dontAsk" }, cwd); return Streaming.StartAsync(Executable, a, c, ct); }
    public Task<StartedProcessHandle> RespawnBackgroundAsync(string cwd, string? jobId, string prompt, CancellationToken ct = default) { var (a, c) = BuildInvocation(jobId is null ? new[] { "--bg", prompt } : new[] { "respawn", jobId }, cwd); return Streaming.StartAsync(Executable, a, c, ct); }
    public Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) => Streaming.WaitForExitAsync(jobId, timeout, ct);
    public Task StopAsync(string jobId, CancellationToken ct = default) => Streaming.StopAsync(jobId, ct);
    public string GetStdoutExcerpt(string jobId) => Streaming.GetStdoutExcerpt(jobId);
    public string GetStderrExcerpt(string jobId) => Streaming.GetStderrExcerpt(jobId);
}

public sealed class WindowsClaudeRuntime : ClaudeRuntimeBase
{
    protected override string Executable => "claude";
    protected override IReadOnlyList<string> PrefixArguments => Array.Empty<string>();
    public override string RuntimeId => "windows";
    public WindowsClaudeRuntime(IProcessRunner runner) : base(runner) { }
}

public sealed class WslClaudeRuntime : ClaudeRuntimeBase
{
    private readonly string _distribution;
    private readonly string _claudeCommand;
    protected override string Executable => "wsl.exe";
    protected override IReadOnlyList<string> PrefixArguments => new[] { "-d", _distribution, "--", _claudeCommand };
    public override string RuntimeId => "wsl:" + _distribution;
    public WslClaudeRuntime(IProcessRunner runner, string distribution, string claudeCommand = "claude") : base(runner) { _distribution = distribution; _claudeCommand = claudeCommand; }
    protected override (IReadOnlyList<string> Args, string Cwd) BuildInvocation(IReadOnlyList<string> args, string cwd)
    {
        var prefix = new List<string> { "--cd", cwd, "-d", _distribution, "--", _claudeCommand };
        return (prefix.Concat(args).ToArray(), Environment.CurrentDirectory);
    }
}
