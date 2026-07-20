using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;
namespace AgentSupervisor.Infrastructure;

public sealed record RecoveryHistoryRecord(long Id, string TaskId, string AttemptId, string Decision, string? FailureCategory, string? Fingerprint, string? GitStatus, string Reason, DateTimeOffset CreatedAt);
public sealed class RecoveryHistoryStore
{
    private readonly string _cs;

    public RecoveryHistoryStore(string cs) => _cs = cs;

    public void Add(string task, string attempt, RecoveryDecision decision, string category, string fingerprint, string git)
    {
        Exec("INSERT INTO recovery_history(task_id,attempt_id,decision,failure_category,fingerprint,git_status,reason,created_at) VALUES($t,$a,$d,$c,$f,$g,$r,$now)",
            ("$t", task), ("$a", attempt), ("$d", decision.Action), ("$c", category),
            ("$f", fingerprint), ("$g", git), ("$r", decision.Reason),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
    }

    public IReadOnlyList<RecoveryHistoryRecord> ForTask(string task) =>
        Read("SELECT id,task_id,attempt_id,decision,failure_category,fingerprint,git_status,reason,created_at FROM recovery_history WHERE task_id=$t ORDER BY id", ("$t", task));

    public decimal TodayCost() =>
        ReadCost("SELECT COALESCE(SUM(CAST(cost_usd AS REAL)),0) FROM task_attempts WHERE started_at >= $d", ("$d", DateTimeOffset.UtcNow.Date.ToString("O")));

    public decimal DailyBudget()
    {
        using var connection = new SqliteConnection(_cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='daily_budget_usd'";
        var value = command.ExecuteScalar();
        connection.Close();
        SqliteConnection.ClearPool(connection);
        return value is null ? 10m : decimal.Parse(value.ToString()!);
    }
    private List<RecoveryHistoryRecord> Read(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var p in ps) q.Parameters.AddWithValue(p.Item1, p.Item2); using var r = q.ExecuteReader(); var a = new List<RecoveryHistoryRecord>(); while (r.Read()) a.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), DateTimeOffset.Parse(r.GetString(8)))); c.Close(); SqliteConnection.ClearPool(c); return a; }
    private decimal ReadCost(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var p in ps) q.Parameters.AddWithValue(p.Item1, p.Item2); var x = Convert.ToDecimal(q.ExecuteScalar()); c.Close(); SqliteConnection.ClearPool(c); return x; }
    private void Exec(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var p in ps) q.Parameters.AddWithValue(p.Item1, p.Item2); q.ExecuteNonQuery(); c.Close(); SqliteConnection.ClearPool(c); }
}
