using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class RuntimePollerAuthExitTests
{
    private sealed class StubAuthRuntime : IClaudeRuntime
    {
        public required string RuntimeId { get; init; }
        public RuntimeCommandResult AuthResult { get; set; } = new("auth", 0, "", "");
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClaudeAgent>>(Array.Empty<ClaudeAgent>());
        public Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("daemon", 0, "", ""));
        public Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken ct = default) => Task.FromResult(AuthResult);
        public Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult<int?>(0);
        public Task StopAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public string GetStdoutExcerpt(string jobId) => "";
        public string GetStderrExcerpt(string jobId) => "";
    }

    private sealed class NoopOutbox : INotificationOutbox
    {
        public void Enqueue(AlertCandidate candidate, int cooldownSeconds) { }
        public Task DeliverPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (SessionSnapshotStore sessions, AppBehaviorSettingsStore behavior) CreateStores()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var connectionString = "Data Source=" + file;
        new SqliteMigrationRunner().Migrate(connectionString);
        return (new SessionSnapshotStore(connectionString), new AppBehaviorSettingsStore(connectionString));
    }

    [Fact]
    public async Task ExitOnAuthFailure_true_requests_exit_on_auth_failure_snapshot()
    {
        var (sessions, behavior) = CreateStores();
        behavior.SetExitOnAuthFailure(true);
        var runtime = new StubAuthRuntime { RuntimeId = "windows", AuthResult = new("auth", 1, "", "not logged in") };
        var shutdown = new AppShutdownCoordinator();
        var exitCount = 0;
        shutdown.ExitAction = () => Interlocked.Increment(ref exitCount);
        var poller = new RuntimePoller(new[] { runtime }, sessions, new AlertEngine(), new NoopOutbox(), behavior, shutdown);

        await poller.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await poller.StopAsync(CancellationToken.None);

        Assert.Equal(1, exitCount);
    }

    [Fact]
    public async Task ExitOnAuthFailure_false_never_requests_exit()
    {
        var (sessions, behavior) = CreateStores();
        behavior.SetExitOnAuthFailure(false);
        var runtime = new StubAuthRuntime { RuntimeId = "windows", AuthResult = new("auth", 1, "", "not logged in") };
        var shutdown = new AppShutdownCoordinator();
        var exitCount = 0;
        shutdown.ExitAction = () => Interlocked.Increment(ref exitCount);
        var poller = new RuntimePoller(new[] { runtime }, sessions, new AlertEngine(), new NoopOutbox(), behavior, shutdown);

        await poller.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await poller.StopAsync(CancellationToken.None);

        Assert.Equal(0, exitCount);
    }
}
