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

    private sealed class RecordingOutbox : INotificationOutbox
    {
        private readonly object _lock = new();
        private readonly List<AlertCandidate> _candidates = new();
        public IReadOnlyList<AlertCandidate> Candidates
        {
            get { lock (_lock) return _candidates.ToArray(); }
        }

        public void Enqueue(AlertCandidate candidate, int cooldownSeconds)
        {
            lock (_lock) _candidates.Add(candidate);
        }

        public Task DeliverPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (SessionSnapshotStore sessions, string databasePath) CreateStores()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var connectionString = "Data Source=" + databasePath;
        new SqliteMigrationRunner().Migrate(connectionString);
        return (new SessionSnapshotStore(connectionString), databasePath);
    }

    [Fact]
    public async Task AuthenticationFailureSnapshot_EnqueuesNotificationWithoutRequestingExit()
    {
        var (sessions, databasePath) = CreateStores();
        try
        {
            var runtime = new StubAuthRuntime { RuntimeId = "windows", AuthResult = new("auth", 1, "", "not logged in") };
            var outbox = new RecordingOutbox();
            var shutdown = new AppShutdownCoordinator();
            var exitCount = 0;
            shutdown.ExitAction = () => Interlocked.Increment(ref exitCount);
            var poller = new RuntimePoller(new[] { runtime }, sessions, new AlertEngine(), outbox);

            await poller.StartAsync(CancellationToken.None);
            await Task.Delay(500);
            await poller.StopAsync(CancellationToken.None);

            Assert.Contains(outbox.Candidates, candidate => candidate.NotificationType == "authentication-failure");
            Assert.Equal(0, exitCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}
