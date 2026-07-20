using System.Security.Cryptography;
using System.Text;
using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;
namespace AgentSupervisor.Infrastructure;

public sealed class HookEventStore
{
    private readonly string _cs; public HookEventStore(string cs) => _cs = cs;
    public bool Save(HookEvent e, string source, string payload, string hash) { using var c = new SqliteConnection(_cs); c.Open(); using var x = c.CreateCommand(); x.CommandText = "INSERT OR IGNORE INTO session_events(runtime_id,session_id,source,source_event_id,hook_event_name,payload_hash,payload_json,occurred_at,received_at) VALUES($r,$s,$o,$i,$n,$h,$p,$t,$now)"; x.Parameters.AddWithValue("$r", e.RuntimeId ?? "unknown"); x.Parameters.AddWithValue("$s", (object?)e.SessionId ?? DBNull.Value); x.Parameters.AddWithValue("$o", source); x.Parameters.AddWithValue("$i", (object?)e.SourceEventId ?? DBNull.Value); x.Parameters.AddWithValue("$n", e.EventName ?? "unknown"); x.Parameters.AddWithValue("$h", hash); x.Parameters.AddWithValue("$p", payload); x.Parameters.AddWithValue("$t", (e.OccurredAt ?? DateTimeOffset.UtcNow).ToString("O")); x.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); var n = x.ExecuteNonQuery(); c.Close(); SqliteConnection.ClearPool(c); return n == 1; }

    // DESIGN.md Phase 3: sessions that only Hooks ever saw (no Agent View poller entry --
    // e.g. a manual interactive `claude` session that isn't a managed background job) should
    // still show up on the dashboard. `knownSessionIds` is whatever the poller-backed
    // SessionSnapshotStore already reports, so this only returns the ones Hooks-only found.
    public IReadOnlyList<ObserveOnlySession> ReadObserveOnly(IReadOnlySet<string> knownSessionIds)
    {
        using var c = new SqliteConnection(_cs); c.Open(); using var x = c.CreateCommand();
        x.CommandText = "SELECT session_id, runtime_id, hook_event_name, occurred_at FROM session_events se WHERE session_id IS NOT NULL AND received_at = (SELECT MAX(received_at) FROM session_events se2 WHERE se2.session_id = se.session_id) ORDER BY occurred_at DESC";
        var output = new List<ObserveOnlySession>(); using var reader = x.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            if (knownSessionIds.Contains(sessionId)) continue;
            output.Add(new(sessionId, reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3))));
        }
        return output;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
