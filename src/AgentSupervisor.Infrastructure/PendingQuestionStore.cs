using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;
namespace AgentSupervisor.Infrastructure;

public sealed class PendingQuestionStore
{
    private readonly string _cs; public PendingQuestionStore(string cs) => _cs = cs;
    public PendingQuestionRecord Create(string runtimeId, string? sessionId, string questionsJson) { var x = new PendingQuestionRecord(Guid.NewGuid().ToString("N"), runtimeId, sessionId, questionsJson, "pending", null, DateTimeOffset.UtcNow, null); Exec("INSERT INTO pending_questions(id,runtime_id,session_id,questions_json,status,created_at) VALUES($id,$r,$s,$q,$st,$c)", ("$id", x.Id), ("$r", runtimeId), ("$s", (object?)sessionId ?? DBNull.Value), ("$q", questionsJson), ("$st", x.Status), ("$c", x.CreatedAt.ToString("O"))); return x; }
    public PendingQuestionRecord? Get(string id) => Read("SELECT id,runtime_id,session_id,questions_json,status,answers_json,created_at,answered_at FROM pending_questions WHERE id=$id", ("$id", id)).SingleOrDefault();
    public IReadOnlyList<PendingQuestionRecord> All() => Read("SELECT id,runtime_id,session_id,questions_json,status,answers_json,created_at,answered_at FROM pending_questions ORDER BY created_at DESC");
    public bool Answer(string id, string answersJson) => Exec("UPDATE pending_questions SET status='answered',answers_json=$a,answered_at=$d WHERE id=$id AND status='pending'", ("$id", id), ("$a", answersJson), ("$d", DateTimeOffset.UtcNow.ToString("O"))) > 0;
    public bool MarkTimedOut(string id) => Exec("UPDATE pending_questions SET status='timed_out',answered_at=$d WHERE id=$id AND status='pending'", ("$id", id), ("$d", DateTimeOffset.UtcNow.ToString("O"))) > 0;
    private List<PendingQuestionRecord> Read(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); using var r = cmd.ExecuteReader(); var a = new List<PendingQuestionRecord>(); while (r.Read()) a.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), DateTimeOffset.Parse(r.GetString(6)), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)))); c.Close(); SqliteConnection.ClearPool(c); return a; }
    private int Exec(string sql, params (string, object)[] ps) { using var c = new SqliteConnection(_cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, p.Item2); var n = cmd.ExecuteNonQuery(); c.Close(); SqliteConnection.ClearPool(c); return n; }
}
