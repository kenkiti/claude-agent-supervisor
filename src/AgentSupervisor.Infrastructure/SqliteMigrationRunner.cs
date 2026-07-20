using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class SqliteMigrationRunner
{
    public static readonly string Schema = """
CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, created_at TEXT NOT NULL, status TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS runtimes (id TEXT PRIMARY KEY, type TEXT NOT NULL, distribution TEXT, claude_command TEXT NOT NULL DEFAULT 'claude');
CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, runtime_id TEXT NOT NULL REFERENCES runtimes(id), cwd TEXT NOT NULL, max_parallel INTEGER NOT NULL, default_mode TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS tasks (id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), prompt TEXT NOT NULL, mode TEXT NOT NULL, status TEXT NOT NULL, max_turns INTEGER NOT NULL, max_budget_usd TEXT NOT NULL, timeout_seconds INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, max_retries INTEGER NOT NULL DEFAULT 3, manual_stop INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS task_attempts (id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), attempt_number INTEGER NOT NULL, started_at TEXT, ended_at TEXT, exit_code INTEGER, status TEXT NOT NULL, stdout_path TEXT, stdout_excerpt TEXT, stderr_path TEXT, stderr_excerpt TEXT, cost_usd TEXT, turns_used INTEGER, error TEXT, failure_category TEXT, fingerprint TEXT, git_status TEXT, claude_session_id TEXT);
CREATE TABLE IF NOT EXISTS recovery_history (id INTEGER PRIMARY KEY AUTOINCREMENT, task_id TEXT NOT NULL, attempt_id TEXT NOT NULL, decision TEXT NOT NULL, failure_category TEXT, fingerprint TEXT, git_status TEXT, reason TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS task_events (id INTEGER PRIMARY KEY AUTOINCREMENT, task_id TEXT NOT NULL, event_type TEXT NOT NULL, payload TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS session_snapshots (id INTEGER PRIMARY KEY AUTOINCREMENT, runtime_id TEXT NOT NULL, captured_at TEXT NOT NULL, payload TEXT NOT NULL, error TEXT);
CREATE TABLE IF NOT EXISTS session_events (id INTEGER PRIMARY KEY AUTOINCREMENT, runtime_id TEXT NOT NULL, session_id TEXT, source TEXT NOT NULL, source_event_id TEXT, hook_event_name TEXT NOT NULL, payload_hash TEXT NOT NULL, payload_json TEXT NOT NULL, occurred_at TEXT NOT NULL, received_at TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_events_source_id ON session_events(runtime_id,source_event_id) WHERE source_event_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_events_hash ON session_events(runtime_id,payload_hash);
CREATE TABLE IF NOT EXISTS notification_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, notification_type TEXT NOT NULL, severity TEXT NOT NULL, runtime_id TEXT NOT NULL, session_id TEXT, state_version INTEGER NOT NULL, channel_id TEXT NOT NULL, payload_json TEXT NOT NULL, created_at TEXT NOT NULL, next_attempt_at TEXT NOT NULL, attempt_count INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending');
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_outbox_dedup ON notification_outbox(notification_type,runtime_id,session_id,state_version,channel_id);
CREATE TABLE IF NOT EXISTS notification_deliveries (id INTEGER PRIMARY KEY AUTOINCREMENT, outbox_id INTEGER NOT NULL, status TEXT NOT NULL, status_code INTEGER, attempt_count INTEGER NOT NULL, error TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS pending_questions (id TEXT PRIMARY KEY, runtime_id TEXT NOT NULL, session_id TEXT, questions_json TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', answers_json TEXT, created_at TEXT NOT NULL, answered_at TEXT);
""";
    public void Migrate(string connectionString, string? backupDirectory = null)
    {
        if (backupDirectory is not null)
        { var dbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource; if (File.Exists(dbPath)) { Directory.CreateDirectory(backupDirectory); File.Copy(dbPath, Path.Combine(backupDirectory, Path.GetFileName(dbPath) + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak")); } }
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using (var inspect = c.CreateCommand())
        {
            inspect.CommandText = "SELECT name FROM pragma_table_info('tasks') WHERE name='manual_stop'";
            if (inspect.ExecuteScalar() is null)
            {
                using var drop = c.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS recovery_history; DROP TABLE IF EXISTS task_attempts; DROP TABLE IF EXISTS tasks; DROP TABLE IF EXISTS projects; DROP TABLE IF EXISTS runtimes;";
                drop.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        c.Close();
        // Microsoft.Data.Sqlite pools native connections by default, which keeps
        // the file handle open after Dispose() and breaks immediate delete/move
        // of the database file (e.g. DESIGN.md 17.3 backup/restore). Migrate()
        // is not a hot path, so clear the pool eagerly instead of leaking the lock.
        SqliteConnection.ClearPool(c);
    }
}
