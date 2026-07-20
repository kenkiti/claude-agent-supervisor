using System.Threading.Channels;
using AgentSupervisor.Core;
using Microsoft.Extensions.Hosting;
namespace AgentSupervisor.Infrastructure;

public sealed class TaskQueue : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly TaskStore _tasks;
    private readonly ProjectRegistryStore _projects;
    private readonly SessionSnapshotStore _sessions;
    private readonly IEnumerable<IClaudeRuntime> _runtimes;
    private readonly RecoveryHistoryStore _history;
    private readonly INotificationOutbox? _outbox;
    private readonly TimeSpan? _backoffOverride;

    public TaskQueue(TaskStore tasks, ProjectRegistryStore projects, SessionSnapshotStore sessions, IEnumerable<IClaudeRuntime> runtimes,
        RecoveryHistoryStore? history = null, INotificationOutbox? outbox = null, TimeSpan? backoffOverride = null)
    {
        _tasks = tasks;
        _projects = projects;
        _sessions = sessions;
        _runtimes = runtimes;
        _history = history ?? new RecoveryHistoryStore(GetConnection(tasks));
        _outbox = outbox;
        _backoffOverride = backoffOverride;
    }
    private static string GetConnection(TaskStore t) => t.ConnectionString;
    public bool Enqueue(string id) => _queue.Writer.TryWrite(id);
    // Capacity and session guards prevent overlapping work in the same project or checkout.
    public static bool ExceedsCapacity(ProjectRecord p, IReadOnlyList<TaskRecord> ts) =>
        ts.Count(x => x.ProjectId == p.Id && x.Status is "running" or "queued") > p.MaxParallel;

    public static bool HasConflictingSession(ProjectRecord p, IReadOnlyList<NormalizedSession> ss) =>
        ss.Any(x => x.Project == p.Cwd && x.LifecycleState is "running" or "starting");
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
            await ProcessAsync(id, ct);
    }
    private async Task ProcessAsync(string id, CancellationToken ct)
    {
        var t = _tasks.Get(id);
        if (t is null)
            return;

        var p = _projects.Projects().FirstOrDefault(x => x.Id == t.ProjectId);
        if (p is null)
        {
            _tasks.Status(id, "failed");
            return;
        }

        if (_history.TodayCost() >= _history.DailyBudget())
        {
            _tasks.Status(id, "failed");
            Notify("daily-budget-exceeded", id);
            return;
        }

        if (ExceedsCapacity(p, _tasks.All()) || HasConflictingSession(p, _sessions.ReadLatest()))
        {
            _tasks.Status(id, "failed");
            return;
        }

        var rt = _runtimes.FirstOrDefault(x => x.RuntimeId == p.RuntimeId);
        if (rt is null)
        {
            _tasks.Status(id, "failed");
            return;
        }

        string? previous = null;
        string? previousSession = null;

        for (var n = 1; ; n++)
        {
            t = _tasks.Get(id)!;
            if (t.ManualStop)
            {
                _tasks.Status(id, "manual");
                return;
            }

            var a = new TaskAttemptRecord(Guid.NewGuid().ToString("N"), id, n, DateTimeOffset.UtcNow, null, null, "running", null, null, null, null, null, null, null);
            _tasks.AddAttempt(a);
            _tasks.Status(id, "running");

            string? job = null;
            int? exit = null;
            string stdout = "";
            string stderr = "";
            string git = "";
            string? session = null;
            string error = "";

            try
            {
                var g = await rt.GetGitStatusAsync(p.Cwd, ct);
                git = g.Output;

                var h = t.Mode == "batch-print" && n > 1 && previousSession is not null
                    ? await rt.ResumeBatchAsync(p.Cwd, previousSession, t.Prompt, t.MaxTurns, t.MaxBudgetUsd, ct)
                    : t.Mode == "batch-print"
                        ? await rt.StartBatchAsync(p.Cwd, t.Prompt, t.MaxTurns, t.MaxBudgetUsd, ct)
                        : n > 1
                            ? await rt.RespawnBackgroundAsync(p.Cwd, null, t.Prompt, ct)
                            : await rt.StartBackgroundAsync(p.Cwd, t.Prompt, ct);

                job = h.JobId;
                exit = await rt.WaitForExitAsync(job, TimeSpan.FromSeconds(t.TimeoutSeconds), ct);
                stdout = rt.GetStdoutExcerpt(job);
                stderr = rt.GetStderrExcerpt(job);

                if (exit is null)
                    await rt.StopAsync(job, ct);

                session = StreamJsonParser.FindSessionId(stdout);
                if (session is not null)
                    previousSession = session;

                error = exit is null
                    ? $"timed out after {t.TimeoutSeconds}s"
                    : exit == 0
                        ? ""
                        : $"exit code {exit}";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                stderr = ex.Message;
            }

            var category = FailureClassifier.Classify(exit, stdout, stderr);
            var fp = FailureClassifier.Fingerprint(category, exit, stderr);
            var success = exit == 0;
            var (cost, turns) = StreamJsonParser.FindResult(stdout);
            _tasks.CompleteAttempt(a, success ? "succeeded" : "failed", exit, stdout, stderr, string.IsNullOrEmpty(error) ? null : error, category, fp, git, session, cost, turns);

            if (success)
            {
                _tasks.Status(id, "succeeded");
                return;
            }

            var d = RecoveryPolicy.Decide(category, n, t.MaxRetries, t.ManualStop, previous == fp, _history.TodayCost() >= _history.DailyBudget());
            _history.Add(id, a.Id, d, category, fp, git);
            previous = fp;

            if (d.Action == "retry")
            {
                _tasks.Status(id, "retrying");
                var delay = _backoffOverride ?? RecoveryPolicy.Backoff(n);
                await Task.Delay(delay, ct);
                continue;
            }

            _tasks.Status(id, d.Action == "budget-exceeded" ? "failed" : d.Action);
            Notify(d.Action == "manual" ? "recovery-manual" : "recovery-exhausted", id);
            return;
        }
    }
    private void Notify(string type, string id) { if (_outbox is null) return; var c = new AlertCandidate(type, "error", "task", id, 0, "{}", new[] { "windows", "discord" }); _outbox.Enqueue(c, 300); }
}
