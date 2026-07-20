using AgentSupervisor.App.Bootstrap;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class FirstRunWizardTests
{
    [Fact]
    public async Task Wizard_reports_scheduler_failure_instead_of_claiming_success()
    {
        var wizard = new FirstRunWizard(); var scheduler = new FailingScheduler();
        var result = await wizard.RunAsync(new RuntimeDiscovery(new FakeRunner()), new[] { ("claude", Array.Empty<string>()) }, () => Task.CompletedTask, scheduler, "app.exe", true);
        Assert.True(result.HooksInstalled); Assert.False(result.AutoStartEnabled); Assert.NotNull(result.Error);
    }

    private sealed class FailingScheduler : ITaskScheduler
    { public Task<bool> RegisterAsync(string executable, bool dryRun = false) => throw new InvalidOperationException("test"); public Task<bool> UnregisterAsync(bool dryRun = false) => Task.FromResult(true); }
    private sealed class FakeRunner : IProcessRunner
    { public Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null) => Task.FromResult(new RuntimeProbeResult(fileName, 0, "ok", "")); }
}
