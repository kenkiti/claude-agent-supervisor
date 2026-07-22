using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class AppShutdownCoordinatorTests
{
    [Fact]
    public void RequestExit_invokes_exit_action_only_once()
    {
        var coordinator = new AppShutdownCoordinator();
        var invocationCount = 0;
        coordinator.ExitAction = () => invocationCount++;

        coordinator.RequestExit();
        coordinator.RequestExit();

        Assert.Equal(1, invocationCount);
    }
}
