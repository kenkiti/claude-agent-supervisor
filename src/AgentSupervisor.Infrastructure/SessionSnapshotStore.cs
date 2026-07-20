using System.Text.Json;
using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class SessionSnapshotStore
{
    private readonly string _connectionString;
    public SessionSnapshotStore(string connectionString) => _connectionString = connectionString;
    public void Save(RuntimeSnapshot snapshot)
    {
        using var c = new SqliteConnection(_connectionString); c.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_snapshots(runtime_id,captured_at,payload,error) VALUES($r,$t,$p,$e)";
        cmd.Parameters.AddWithValue("$r", snapshot.RuntimeId); cmd.Parameters.AddWithValue("$t", snapshot.CapturedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(snapshot)); cmd.Parameters.AddWithValue("$e", (object?)snapshot.Error ?? DBNull.Value); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<NormalizedSession> ReadLatest()
    {
        using var c = new SqliteConnection(_connectionString); c.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM session_snapshots ORDER BY captured_at DESC";
        var seen = new HashSet<string>(); var output = new List<NormalizedSession>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) { var s = JsonSerializer.Deserialize<RuntimeSnapshot>(reader.GetString(0)); if (s is null) continue; foreach (var a in s.Agents) { var n = SessionNormalizer.Normalize(a, s.RuntimeId, s.CapturedAt); if (seen.Add(n.Id)) output.Add(n); } }
        return output;
    }
}
