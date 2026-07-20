using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class Phase6UnitTests
{
    private sealed class StubClaudeRuntime : IClaudeRuntime
    {
        public required string RuntimeId { get; init; }
        public int ExitCode { get; set; } = 1;
        public int Starts { get; private set; }
        public Action? OnWait { get; set; }
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClaudeAgent>>(Array.Empty<ClaudeAgent>());
        public Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("daemon", 0, "", ""));
        public Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("auth", 0, "", ""));
        public Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken ct = default) { Starts++; return Task.FromResult(new StartedProcessHandle("job", 1)); }
        public Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) { Starts++; return Task.FromResult(new StartedProcessHandle("job", 1)); }
        public Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) { OnWait?.Invoke(); return Task.FromResult<int?>(ExitCode); }
        public Task StopAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public string GetStdoutExcerpt(string jobId) => "";
        public string GetStderrExcerpt(string jobId) => Starts switch
        {
            1 => "rate limit a",
            2 => "rate limit b",
            _ => "rate limit c"
        };
    }

    [Fact]
    public async Task TaskQueue_retry_then_exhausted_records_terminal_failure()
    {
        using var fixture = new QueueFixture();
        var runtime = new StubClaudeRuntime { RuntimeId = "windows", ExitCode = 1 };
        var queue = fixture.CreateQueue(runtime, TimeSpan.Zero);
        var task = fixture.Tasks.Add("project", "retry", "batch-print", 1, 1m, 5);
        await queue.StartAsync(CancellationToken.None);
        try { queue.Enqueue(task.Id); var result = await fixture.WaitForTerminal(task.Id); Assert.Equal("exhausted", result.Status); Assert.Equal(3, fixture.Tasks.Attempts(task.Id).Count); }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TaskQueue_rejects_when_daily_budget_is_exceeded_without_starting_runtime()
    {
        using var fixture = new QueueFixture();
        fixture.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES('daily_budget_usd','1')");
        var prior = fixture.Tasks.Add("project", "prior", "batch-print", 1, 1m, 5);
        fixture.Execute($"INSERT INTO task_attempts(id,task_id,attempt_number,started_at,status,cost_usd) VALUES('prior-attempt','{prior.Id}',1,'{DateTimeOffset.UtcNow:O}','failed','2')");
        var runtime = new StubClaudeRuntime { RuntimeId = "windows" };
        var queue = fixture.CreateQueue(runtime, TimeSpan.Zero);
        var task = fixture.Tasks.Add("project", "rejected", "batch-print", 1, 1m, 5);
        await queue.StartAsync(CancellationToken.None);
        try { queue.Enqueue(task.Id); Assert.Equal("failed", (await fixture.WaitForTerminal(task.Id)).Status); Assert.Equal(0, runtime.Starts); }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TaskQueue_manual_stop_ends_after_one_failed_attempt_without_retry()
    {
        using var fixture = new QueueFixture();
        var runtime = new StubClaudeRuntime { RuntimeId = "windows", ExitCode = 1 };
        runtime.OnWait = () => fixture.Tasks.ManualStop(fixture.CurrentTaskId!);
        var queue = fixture.CreateQueue(runtime, TimeSpan.Zero);
        var task = fixture.Tasks.Add("project", "stop", "batch-print", 1, 1m, 5);
        fixture.CurrentTaskId = task.Id;
        await queue.StartAsync(CancellationToken.None);
        try { queue.Enqueue(task.Id); Assert.Equal("manual", (await fixture.WaitForTerminal(task.Id)).Status); Assert.Single(fixture.Tasks.Attempts(task.Id)); Assert.Equal(1, runtime.Starts); }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    private sealed class QueueFixture : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        public QueueFixture() { var connectionString = "Data Source=" + _file; new SqliteMigrationRunner().Migrate(connectionString); Tasks = new TaskStore(connectionString); var projects = new ProjectRegistryStore(connectionString); projects.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude")); projects.AddProject(new ProjectRecord("project", "windows", @"C:\work", 1, "batch-print")); Projects = projects; Sessions = new SessionSnapshotStore(connectionString); }
        public TaskStore Tasks { get; }
        public ProjectRegistryStore Projects { get; }
        public SessionSnapshotStore Sessions { get; }
        public string? CurrentTaskId { get; set; }
        public void Execute(string sql) { using var connection = new SqliteConnection("Data Source=" + _file); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public TaskQueue CreateQueue(StubClaudeRuntime runtime, TimeSpan delay) => new(Tasks, Projects, Sessions, new IClaudeRuntime[] { runtime }, backoffOverride: delay);
        public async Task<TaskRecord> WaitForTerminal(string id) { var end = DateTimeOffset.UtcNow.AddSeconds(5); while (DateTimeOffset.UtcNow < end) { var task = Tasks.Get(id)!; if (task.Status is "failed" or "exhausted" or "manual" or "succeeded") return task; await Task.Delay(10); } throw new TimeoutException(); }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (File.Exists(_file)) File.Delete(_file); }
    }
    [Theory]
    [InlineData("rate limit", "retry")]
    [InlineData("overloaded", "retry")]
    [InlineData("HTTP 503", "retry")]
    [InlineData("network ECONNRESET", "retry")]
    [InlineData("daemon stopped", "retry")]
    [InlineData("authentication failed", "manual")]
    [InlineData("billing payment required", "manual")]
    [InlineData("permission denied", "manual")]
    [InlineData("tests failed", "manual")]
    [InlineData("compilation error", "manual")]
    [InlineData("merge conflict", "manual")]
    public void Classifier_table(string message, string expected) => Assert.Equal(expected, FailureClassifier.Classify(1, null, message));

    [Fact] public void Timeout_is_retry_candidate() => Assert.Equal("retry", FailureClassifier.Classify(null, null, ""));
    [Fact] public void Unknown_failure_is_manual() => Assert.Equal("manual", FailureClassifier.Classify(1, null, "unexpected failure"));
    [Fact] public void Fingerprint_normalizes_digits() => Assert.Equal(FailureClassifier.Fingerprint("retry", 1, "line 12 failed"), FailureClassifier.Fingerprint("retry", 1, "line 99 failed"));
    [Fact] public void Backoff_is_exponential_and_capped() { Assert.Equal(TimeSpan.FromSeconds(30), RecoveryPolicy.Backoff(1)); Assert.Equal(TimeSpan.FromSeconds(60), RecoveryPolicy.Backoff(2)); Assert.Equal(TimeSpan.FromMinutes(30), RecoveryPolicy.Backoff(20)); }
    [Fact] public void Auth_never_retries() => Assert.Equal("manual", RecoveryPolicy.Decide("manual", 1, 3, false, false, false).Action);
    [Fact] public void Same_fingerprint_exhausts() => Assert.Equal("exhausted", RecoveryPolicy.Decide("retry", 1, 3, false, true, false).Action);
    [Fact] public void Manual_stop_wins() => Assert.Equal("manual", RecoveryPolicy.Decide("retry", 1, 3, true, false, false).Action);
    [Fact] public void Budget_wins() => Assert.Equal("budget-exceeded", RecoveryPolicy.Decide("retry", 1, 3, false, false, true).Action);
}
