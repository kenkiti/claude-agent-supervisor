using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class TaskStore
{
    private readonly string _cs; internal string ConnectionString => _cs; public TaskStore(string cs) => _cs = cs;
    public TaskRecord Add(string projectId, string prompt, string mode, int turns, decimal budget, int timeout) { var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow; Exec("INSERT INTO tasks(id,project_id,prompt,mode,status,max_turns,max_budget_usd,timeout_seconds,created_at,updated_at) VALUES($id,$p,$prompt,$mode,'queued',$turns,$budget,$timeout,$now,$now)", ("$id", id), ("$p", projectId), ("$prompt", prompt), ("$mode", mode), ("$turns", turns), ("$budget", budget), ("$timeout", timeout), ("$now", now.ToString("O"))); return new(id, projectId, prompt, mode, "queued", turns, budget, timeout, now, now); }
    public IReadOnlyList<TaskRecord> All() => Read("SELECT id,project_id,prompt,mode,status,max_turns,max_budget_usd,timeout_seconds,created_at,updated_at,max_retries,manual_stop FROM tasks ORDER BY created_at DESC");
    public TaskRecord? Get(string id) => Read("SELECT id,project_id,prompt,mode,status,max_turns,max_budget_usd,timeout_seconds,created_at,updated_at,max_retries,manual_stop FROM tasks WHERE id=$id", ("$id", id)).FirstOrDefault();
    public void Status(string id, string status) => Exec("UPDATE tasks SET status=$s,updated_at=$now WHERE id=$id", ("$s", status), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
    public void ManualStop(string id) => Exec("UPDATE tasks SET manual_stop=1,updated_at=$now WHERE id=$id", ("$id", id), ("$now", DateTimeOffset.UtcNow.ToString("O")));
    public IReadOnlyList<TaskAttemptRecord> Attempts(string id) => ReadAttempts("SELECT id,task_id,attempt_number,started_at,ended_at,exit_code,status,stdout_path,stdout_excerpt,stderr_path,stderr_excerpt,cost_usd,turns_used,error,failure_category,fingerprint,git_status,claude_session_id FROM task_attempts WHERE task_id=$id ORDER BY attempt_number", ("$id", id));
    public void AddAttempt(TaskAttemptRecord a) => Exec("INSERT INTO task_attempts(id,task_id,attempt_number,started_at,status) VALUES($id,$t,$n,$start,$status)", ("$id", a.Id), ("$t", a.TaskId), ("$n", a.AttemptNumber), ("$start", a.StartedAt?.ToString("O") ?? (object)DBNull.Value), ("$status", a.Status));
    public void CompleteAttempt(TaskAttemptRecord attempt, string status, int? exit, string? stdout, string? stderr, string? error, string category, string fingerprint, string git, string? session, decimal? cost = null, int? turns = null)
    {
        Exec("UPDATE task_attempts SET status=$status,ended_at=$end,exit_code=$exit,stdout_excerpt=$oe,stderr_excerpt=$ee,error=$error,failure_category=$cat,fingerprint=$fp,git_status=$git,claude_session_id=$sid,cost_usd=$cost,turns_used=$turns WHERE id=$id",
            ("$id", attempt.Id), ("$status", status), ("$end", DateTimeOffset.UtcNow.ToString("O")),
            ("$exit", (object?)exit ?? DBNull.Value), ("$oe", (object?)stdout ?? DBNull.Value),
            ("$ee", (object?)stderr ?? DBNull.Value), ("$error", (object?)error ?? DBNull.Value),
            ("$cat", category), ("$fp", fingerprint), ("$git", git), ("$sid", (object?)session ?? DBNull.Value),
            ("$cost", (object?)cost ?? DBNull.Value), ("$turns", (object?)turns ?? DBNull.Value));
    }
    public void CompleteAttempt(string id, string status, int? exitCode, string? stdoutExcerpt, string? stderrExcerpt, string? error)
    {
        Exec("UPDATE task_attempts SET status=$status,ended_at=$end,exit_code=$exit,stdout_excerpt=$oe,stderr_excerpt=$ee,error=$error WHERE id=$id",
            ("$id", id), ("$status", status), ("$end", DateTimeOffset.UtcNow.ToString("O")),
            ("$exit", (object?)exitCode ?? DBNull.Value), ("$oe", (object?)stdoutExcerpt ?? DBNull.Value),
            ("$ee", (object?)stderrExcerpt ?? DBNull.Value), ("$error", (object?)error ?? DBNull.Value));
    }
    private List<TaskRecord> Read(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); using var r = cmd.ExecuteReader(); var a = new List<TaskRecord>(); while (r.Read()) a.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetDecimal(6), r.GetInt32(7), DateTimeOffset.Parse(r.GetString(8)), DateTimeOffset.Parse(r.GetString(9)), r.GetInt32(10), r.GetInt32(11) != 0)); c.Close(); SqliteConnection.ClearPool(c); return a; }
    private List<TaskAttemptRecord> ReadAttempts(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); using var r = cmd.ExecuteReader(); var a = new List<TaskAttemptRecord>(); while (r.Read()) a.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), D(r, 3), D(r, 4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6), S(r, 7), S(r, 8), S(r, 9), S(r, 10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetInt32(12), S(r, 13), S(r, 14), S(r, 15), S(r, 16), S(r, 17))); c.Close(); SqliteConnection.ClearPool(c); return a; }
    private static DateTimeOffset? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i)); private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private void Exec(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); cmd.ExecuteNonQuery(); c.Close(); SqliteConnection.ClearPool(c); }
}
