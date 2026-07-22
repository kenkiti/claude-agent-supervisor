using Microsoft.Data.Sqlite;

namespace AgentSupervisor.Infrastructure;

public sealed class NotificationChannelSettingsStore
{
    private readonly string _connectionString;

    public NotificationChannelSettingsStore(string connectionString) => _connectionString = connectionString;

    public bool IsEnabled(string channelId)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", "notification_channel_enabled:" + channelId);
        var value = command.ExecuteScalar() as string;
        c.Close();
        SqliteConnection.ClearPool(c);
        return value is null || value == "true";
    }

    public void SetEnabled(string channelId, bool enabled)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        command.Parameters.AddWithValue("$key", "notification_channel_enabled:" + channelId);
        command.Parameters.AddWithValue("$value", enabled ? "true" : "false");
        command.ExecuteNonQuery();
        c.Close();
        SqliteConnection.ClearPool(c);
    }
}

public sealed record EnabledSubmission(bool Enabled);
