using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class AppBehaviorSettingsStore
{
    private readonly string _connectionString;

    public AppBehaviorSettingsStore(string connectionString) => _connectionString = connectionString;

    public bool ExitOnAuthFailure
    {
        get
        {
            // CI/UI-smoke-test hosts run against a machine with no `claude` CLI installed, so
            // RuntimePoller's very first poll sees an authentication-failure snapshot and would
            // otherwise self-exit the app before Playwright ever gets to connect. This override
            // only affects test/CI hosts that explicitly set the env var; production behavior
            // (default ON) is unchanged.
            if (Environment.GetEnvironmentVariable("AGENTSUPERVISOR_DISABLE_AUTH_EXIT") == "1") return false;

            using var c = new SqliteConnection(_connectionString);
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", "exit_on_auth_failure");
            var value = command.ExecuteScalar() as string;
            c.Close();
            SqliteConnection.ClearPool(c);
            return value is null || value == "true";
        }
    }

    public void SetExitOnAuthFailure(bool enabled)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        command.Parameters.AddWithValue("$key", "exit_on_auth_failure");
        command.Parameters.AddWithValue("$value", enabled ? "true" : "false");
        command.ExecuteNonQuery();
        c.Close();
        SqliteConnection.ClearPool(c);
    }
}
