using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

// The concrete SQLite-backed INotificationOutbox. Kept separate from Notifications.cs
// (which only had the interface + channel classes when this was picked up) so cooldown,
// retry/backoff, and delivery logging live in one place next to the schema they depend on.
public sealed class SqliteNotificationOutbox : INotificationOutbox
{
    private readonly string _connectionString;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly Func<string, bool>? _channelEnabled;
    private const int MaxAttempts = 5;
    private const int TaskCompletedSuppressWindowSeconds = 90;

    public SqliteNotificationOutbox(string connectionString, IEnumerable<INotificationChannel> channels, Func<string, bool>? channelEnabled = null)
    {
        _connectionString = connectionString;
        _channels = channels;
        _channelEnabled = channelEnabled;
    }

    // Cooldown: if the same (notification_type, runtime_id, session_id, channel) already has a
    // row created within the last `cooldownSeconds`, skip enqueueing a new one even if the
    // state_version differs (state_version-exact duplicates are additionally caught by the
    // unique index via INSERT OR IGNORE, which handles retries of the identical event).
    public void Enqueue(AlertCandidate candidate, int cooldownSeconds)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        if (candidate.NotificationType == "input-needed")
        {
            using var suppressionCmd = c.CreateCommand();
            suppressionCmd.CommandText = "SELECT COUNT(*) FROM notification_outbox WHERE notification_type='task-completed' AND runtime_id=$runtime AND (session_id=$session OR (session_id IS NULL AND $session IS NULL)) AND created_at > $since";
            suppressionCmd.Parameters.AddWithValue("$runtime", candidate.RuntimeId);
            suppressionCmd.Parameters.AddWithValue("$session", (object?)candidate.SessionId ?? DBNull.Value);
            suppressionCmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddSeconds(-TaskCompletedSuppressWindowSeconds).ToString("O"));
            var hasRecentTaskCompleted = Convert.ToInt64(suppressionCmd.ExecuteScalar()) > 0;
            if (hasRecentTaskCompleted) return;
        }
        foreach (var channelId in candidate.Channels)
        {
            if (_channelEnabled is not null && !_channelEnabled(channelId)) continue;
            using (var cooldownCmd = c.CreateCommand())
            {
                cooldownCmd.CommandText = "SELECT COUNT(*) FROM notification_outbox WHERE notification_type=$type AND runtime_id=$runtime AND (session_id=$session OR (session_id IS NULL AND $session IS NULL)) AND channel_id=$channel AND created_at > $since";
                cooldownCmd.Parameters.AddWithValue("$type", candidate.NotificationType);
                cooldownCmd.Parameters.AddWithValue("$runtime", candidate.RuntimeId);
                cooldownCmd.Parameters.AddWithValue("$session", (object?)candidate.SessionId ?? DBNull.Value);
                cooldownCmd.Parameters.AddWithValue("$channel", channelId);
                cooldownCmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddSeconds(-cooldownSeconds).ToString("O"));
                var withinCooldown = Convert.ToInt64(cooldownCmd.ExecuteScalar()) > 0;
                if (withinCooldown) continue;
            }

            using var insert = c.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO notification_outbox(notification_type,severity,runtime_id,session_id,state_version,channel_id,payload_json,created_at,next_attempt_at,attempt_count,status) VALUES($type,$severity,$runtime,$session,$version,$channel,$payload,$now,$now,0,'pending')";
            insert.Parameters.AddWithValue("$type", candidate.NotificationType);
            insert.Parameters.AddWithValue("$severity", candidate.Severity);
            insert.Parameters.AddWithValue("$runtime", candidate.RuntimeId);
            insert.Parameters.AddWithValue("$session", (object?)candidate.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$version", candidate.StateVersion);
            insert.Parameters.AddWithValue("$channel", channelId);
            insert.Parameters.AddWithValue("$payload", candidate.PayloadJson);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        c.Close();
        SqliteConnection.ClearPool(c);
    }

    public async Task DeliverPendingAsync(CancellationToken cancellationToken)
    {
        var due = ReadDue();
        foreach (var row in due)
        {
            var channel = _channels.FirstOrDefault(x => x.Id == row.ChannelId);
            if (channel is null) { MarkFailed(row.Id, row.AttemptCount, null, "no channel registered for id '" + row.ChannelId + "'"); continue; }
            var candidate = new AlertCandidate(row.NotificationType, row.Severity, row.RuntimeId, row.SessionId, row.StateVersion, row.PayloadJson, new[] { row.ChannelId });
            try
            {
                await channel.SendAsync(candidate, cancellationToken);
                MarkSent(row.Id, row.AttemptCount);
            }
            catch (TransientNotificationException ex)
            {
                var nextAttempt = row.AttemptCount + 1;
                if (nextAttempt >= MaxAttempts) MarkFailed(row.Id, nextAttempt, (int)ex.StatusCode, ex.Message);
                else ScheduleRetry(row.Id, nextAttempt, (int)ex.StatusCode, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-transient (config error, 4xx other than 429, etc.) -- do not retry.
                MarkFailed(row.Id, row.AttemptCount + 1, null, ex.Message);
            }
        }
    }

    private sealed record OutboxRow(long Id, string NotificationType, string Severity, string RuntimeId, string? SessionId, long StateVersion, string ChannelId, string PayloadJson, int AttemptCount);

    private List<OutboxRow> ReadDue()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,notification_type,severity,runtime_id,session_id,state_version,channel_id,payload_json,attempt_count FROM notification_outbox WHERE status='pending' AND next_attempt_at<=$now ORDER BY created_at";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var rows = new List<OutboxRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        c.Close();
        SqliteConnection.ClearPool(c);
        return rows;
    }

    private void MarkSent(long id, int attemptCount)
    {
        Update(id, "sent", attemptCount + 1, null);
        Log(id, "sent", null, attemptCount + 1, null);
    }

    private void MarkFailed(long id, int attemptCount, int? statusCode, string? error)
    {
        Update(id, "failed", attemptCount, null);
        Log(id, "failed", statusCode, attemptCount, error);
    }

    private void ScheduleRetry(long id, int attemptCount, int statusCode, string error)
    {
        // Exponential backoff: 2^attempt seconds, capped at 5 minutes.
        var delaySeconds = Math.Min(300, (int)Math.Pow(2, attemptCount));
        var next = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        Update(id, "pending", attemptCount, next);
        Log(id, "retry", statusCode, attemptCount, error);
    }

    private void Update(long id, string status, int attemptCount, DateTimeOffset? nextAttemptAt)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE notification_outbox SET status=$status, attempt_count=$attempts, next_attempt_at=COALESCE($next, next_attempt_at) WHERE id=$id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$attempts", attemptCount);
        cmd.Parameters.AddWithValue("$next", (object?)nextAttemptAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        c.Close();
        SqliteConnection.ClearPool(c);
    }

    private void Log(long outboxId, string status, int? statusCode, int attemptCount, string? error)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO notification_deliveries(outbox_id,status,status_code,attempt_count,error,created_at) VALUES($outbox,$status,$code,$attempts,$error,$now)";
        cmd.Parameters.AddWithValue("$outbox", outboxId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$code", (object?)statusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempts", attemptCount);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        c.Close();
        SqliteConnection.ClearPool(c);
    }
}
