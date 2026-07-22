using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class NotificationChannelToggleTests
{
    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        WithStore(store => Assert.True(store.IsEnabled("unknown")));
    }

    [Fact]
    public void SetEnabled_TogglesChannel()
    {
        WithStore(store =>
        {
            store.SetEnabled("discord", false);
            Assert.False(store.IsEnabled("discord"));
            store.SetEnabled("discord", true);
            Assert.True(store.IsEnabled("discord"));
        });
    }

    [Fact]
    public void Outbox_SkipsDisabledChannels()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var connectionString = "Data Source=" + path;
        try
        {
            new SqliteMigrationRunner().Migrate(connectionString);
            var outbox = new SqliteNotificationOutbox(connectionString, Array.Empty<INotificationChannel>(), channelId => channelId != "discord");
            var candidate = new AlertCandidate("task-completed", "info", "runtime", "session", 1, "{}", new[] { "discord", "windows" });

            outbox.Enqueue(candidate, 60);

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT channel_id FROM notification_outbox";
            using var reader = command.ExecuteReader();
            var channelIds = new List<string>();
            while (reader.Read()) channelIds.Add(reader.GetString(0));
            Assert.Equal(new[] { "windows" }, channelIds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WithStore(Action<NotificationChannelSettingsStore> action)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var connectionString = "Data Source=" + path;
        try
        {
            new SqliteMigrationRunner().Migrate(connectionString);
            action(new NotificationChannelSettingsStore(connectionString));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
