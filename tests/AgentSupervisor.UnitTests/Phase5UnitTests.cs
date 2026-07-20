using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class Phase5UnitTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    private sealed class StubClaudeRuntime : IClaudeRuntime
    {
        public required string RuntimeId { get; init; }
        public int? ExitCodeToReturn { get; set; } = 0;
        public List<string> StartedModes { get; } = new();

        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClaudeAgent>>(Array.Empty<ClaudeAgent>());
        public Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("daemon", 0, "", ""));
        public Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("auth", 0, "", ""));
        public Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken ct = default)
        { StartedModes.Add("background"); return Task.FromResult(new StartedProcessHandle("job-" + Guid.NewGuid().ToString("N"), 1234)); }
        public Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default)
        { StartedModes.Add("batch-print"); return Task.FromResult(new StartedProcessHandle("job-" + Guid.NewGuid().ToString("N"), 1234)); }
        public Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(ExitCodeToReturn);
        public Task StopAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public string GetStdoutExcerpt(string jobId) => "stdout";
        public string GetStderrExcerpt(string jobId) => "";
    }

    // --- TaskQueue guard logic (pure functions) ---

    [Fact]
    public void ExceedsCapacity_rejects_second_task_when_one_already_running_or_queued()
    {
        var project = new ProjectRecord("proj-1", "windows", @"C:\work\sample-project", MaxParallel: 1, DefaultMode: "batch-print");
        var tasks = new List<TaskRecord>
        {
            new("t1", "proj-1", "do work", "batch-print", "running", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("t2", "proj-1", "do more work", "batch-print", "queued", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        };
        Assert.True(TaskQueue.ExceedsCapacity(project, tasks));
    }

    [Fact]
    public void ExceedsCapacity_allows_new_task_once_prior_task_finished()
    {
        var project = new ProjectRecord("proj-1", "windows", @"C:\work\sample-project", MaxParallel: 1, DefaultMode: "batch-print");
        var tasks = new List<TaskRecord>
        {
            new("t1", "proj-1", "do work", "batch-print", "succeeded", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("t2", "proj-1", "do more work", "batch-print", "queued", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        };
        Assert.False(TaskQueue.ExceedsCapacity(project, tasks));
    }

    [Fact]
    public void ExceedsCapacity_ignores_other_projects()
    {
        var project = new ProjectRecord("proj-1", "windows", @"C:\work\sample-project", MaxParallel: 1, DefaultMode: "batch-print");
        var tasks = new List<TaskRecord>
        {
            new("t1", "proj-2", "unrelated", "batch-print", "running", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("t2", "proj-1", "do work", "batch-print", "queued", 10, 1m, 3600, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        };
        Assert.False(TaskQueue.ExceedsCapacity(project, tasks));
    }

    [Fact]
    public void HasConflictingSession_detects_live_session_in_same_cwd()
    {
        var project = new ProjectRecord("proj-1", "wsl:Ubuntu", "/home/user/sample-app", MaxParallel: 2, DefaultMode: "batch-print");
        var sessions = new List<NormalizedSession>
        {
            new("s1", "/home/user/sample-app", "wsl:Ubuntu", "running", "active", "none", "healthy", DateTimeOffset.UtcNow, "agent-view", "high"),
        };
        Assert.True(TaskQueue.HasConflictingSession(project, sessions));
    }

    [Fact]
    public void HasConflictingSession_ignores_unrelated_cwd()
    {
        var project = new ProjectRecord("proj-1", "wsl:Ubuntu", "/home/user/sample-app", MaxParallel: 2, DefaultMode: "batch-print");
        var sessions = new List<NormalizedSession>
        {
            new("s1", "/home/user/other-project", "wsl:Ubuntu", "running", "active", "none", "healthy", DateTimeOffset.UtcNow, "agent-view", "high"),
        };
        Assert.False(TaskQueue.HasConflictingSession(project, sessions));
    }

    // --- TaskStore / ProjectRegistryStore round trips ---

    [Fact]
    public void TaskStore_add_status_and_attempt_round_trip()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var projects = new ProjectRegistryStore(cs);
            projects.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude"));
            projects.AddProject(new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 1, "batch-print"));
            var store = new TaskStore(cs);
            var created = store.Add("sample-project", "check prices", "batch-print", 10, 2.5m, 600);

            Assert.Equal("queued", created.Status);
            Assert.Single(store.All());
            Assert.Equal(created.Id, store.Get(created.Id)!.Id);

            store.Status(created.Id, "running");
            Assert.Equal("running", store.Get(created.Id)!.Status);

            var attempt = new TaskAttemptRecord(Guid.NewGuid().ToString("N"), created.Id, 1, DateTimeOffset.UtcNow, null, null, "running", null, null, null, null, null, null, null);
            store.AddAttempt(attempt);
            store.CompleteAttempt(attempt.Id, "failed", exitCode: 1, stdoutExcerpt: "out", stderrExcerpt: "boom", error: "exit code 1");

            var attempts = store.Attempts(created.Id);
            Assert.Single(attempts);
            Assert.Equal("failed", attempts[0].Status);
            Assert.Equal(1, attempts[0].ExitCode);
            Assert.Equal("boom", attempts[0].StderrExcerpt);
            Assert.Equal("exit code 1", attempts[0].Error);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void StreamJsonParser_FindResult_extracts_cost_and_turns_from_real_shaped_output()
    {
        var stdout = "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"BATCHOK\"}]}}\n{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"total_cost_usd\":0.0557569,\"result\":\"BATCHOK\"}\n";
        var (cost, turns) = StreamJsonParser.FindResult(stdout);
        Assert.Equal(0.0557569m, cost);
        Assert.Equal(1, turns);
    }

    [Fact]
    public void TaskStore_CompleteAttempt_persists_cost_and_turns()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var projects = new ProjectRegistryStore(cs);
            projects.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude"));
            projects.AddProject(new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 1, "batch-print"));
            var store = new TaskStore(cs);
            var created = store.Add("sample-project", "check prices", "batch-print", 10, 2.5m, 600);
            var attempt = new TaskAttemptRecord(Guid.NewGuid().ToString("N"), created.Id, 1, DateTimeOffset.UtcNow, null, null, "running", null, null, null, null, null, null, null);
            store.AddAttempt(attempt);
            store.CompleteAttempt(attempt, "succeeded", 0, "out", "", null, "none", "fp", "", "session-1", 0.05m, 3);

            var attempts = store.Attempts(created.Id);
            Assert.Single(attempts);
            Assert.Equal(0.05m, attempts[0].CostUsd);
            Assert.Equal(3, attempts[0].TurnsUsed);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void ProjectRegistryStore_add_update_delete_round_trip()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var store = new ProjectRegistryStore(cs);
            store.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude"));
            store.AddRuntime(new RuntimeRecord("wsl:Ubuntu", "wsl", "Ubuntu", "claude"));
            Assert.Equal(2, store.Runtimes().Count);

            store.AddProject(new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 1, "batch-print"));
            Assert.Single(store.Projects());

            store.UpdateProject("sample-project", new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 3, "native-background"));
            var updated = store.Projects().Single(p => p.Id == "sample-project");
            Assert.Equal(3, updated.MaxParallel);
            Assert.Equal("native-background", updated.DefaultMode);

            store.DeleteProject("sample-project");
            Assert.Empty(store.Projects());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    // --- TaskQueue end-to-end wiring (stub runtime, no real process spawned) ---

    [Fact]
    public async Task TaskQueue_end_to_end_marks_task_succeeded_after_stub_runtime_exits_zero()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var taskStore = new TaskStore(cs);
            var projectStore = new ProjectRegistryStore(cs);
            var sessionStore = new SessionSnapshotStore(cs);
            projectStore.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude"));
            projectStore.AddProject(new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 1, "batch-print"));
            var runtime = new StubClaudeRuntime { RuntimeId = "windows", ExitCodeToReturn = 0 };
            var queue = new TaskQueue(taskStore, projectStore, sessionStore, new IClaudeRuntime[] { runtime });

            var task = taskStore.Add("sample-project", "check prices", "batch-print", 10, 1m, 5);
            await queue.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(queue.Enqueue(task.Id));
                var final = await WaitForTerminalStatusAsync(taskStore, task.Id);
                Assert.Equal("succeeded", final.Status);
            }
            finally { await queue.StopAsync(CancellationToken.None); }

            var attempts = taskStore.Attempts(task.Id);
            Assert.Single(attempts);
            Assert.Equal("succeeded", attempts[0].Status);
            Assert.Equal(0, attempts[0].ExitCode);
            Assert.Equal(new[] { "batch-print" }, runtime.StartedModes);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task TaskQueue_end_to_end_marks_second_submission_failed_when_project_at_capacity()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var taskStore = new TaskStore(cs);
            var projectStore = new ProjectRegistryStore(cs);
            var sessionStore = new SessionSnapshotStore(cs);
            projectStore.AddRuntime(new RuntimeRecord("windows", "windows", null, "claude"));
            projectStore.AddProject(new ProjectRecord("sample-project", "windows", @"C:\work\sample-project", 1, "batch-print"));

            // Both tasks are inserted ("queued") before the queue processes either one, simulating
            // two near-simultaneous submissions against a MaxParallel=1 project.
            var task1 = taskStore.Add("sample-project", "check prices", "batch-print", 10, 1m, 5);
            var task2 = taskStore.Add("sample-project", "check prices again", "batch-print", 10, 1m, 5);

            var runtime = new StubClaudeRuntime { RuntimeId = "windows", ExitCodeToReturn = 0 };
            var queue = new TaskQueue(taskStore, projectStore, sessionStore, new IClaudeRuntime[] { runtime });

            await queue.StartAsync(CancellationToken.None);
            try
            {
                queue.Enqueue(task1.Id);
                queue.Enqueue(task2.Id);
                var final1 = await WaitForTerminalStatusAsync(taskStore, task1.Id);
                var final2 = await WaitForTerminalStatusAsync(taskStore, task2.Id);

                // Exactly one of the two same-project submissions is allowed to run; the other is rejected.
                var statuses = new[] { final1.Status, final2.Status };
                Assert.Contains("failed", statuses);
                Assert.Single(statuses, s => s == "succeeded");
            }
            finally { await queue.StopAsync(CancellationToken.None); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    private static async Task<TaskRecord> WaitForTerminalStatusAsync(TaskStore store, string taskId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = store.Get(taskId)!;
            if (current.Status is "succeeded" or "failed") return current;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Task {taskId} did not reach a terminal status in time.");
    }
}
