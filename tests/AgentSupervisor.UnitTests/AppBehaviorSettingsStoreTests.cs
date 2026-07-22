using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class AppBehaviorSettingsStoreTests
{
    [Fact]
    public void ExitOnAuthFailure_defaults_to_false_and_can_be_changed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "agentsupervisor-behavior-" + Guid.NewGuid() + ".db");
        var connectionString = $"Data Source={databasePath}";
        try
        {
            new SqliteMigrationRunner().Migrate(connectionString);
            var store = new AppBehaviorSettingsStore(connectionString);

            Assert.True(store.ExitOnAuthFailure);

            store.SetExitOnAuthFailure(true);
            Assert.True(store.ExitOnAuthFailure);

            store.SetExitOnAuthFailure(false);
            Assert.False(store.ExitOnAuthFailure);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}
