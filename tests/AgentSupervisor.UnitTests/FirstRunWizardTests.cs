using AgentSupervisor.App.Bootstrap;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class FirstRunWizardTests
{
    [Fact]
    public async Task Wizard_completes_without_attempting_auto_start_registration()
    {
        var wizard = new FirstRunWizard();
        var result = await wizard.RunAsync(new RuntimeDiscovery(new FakeRunner()), new[] { ("claude", Array.Empty<string>()) }, () => Task.CompletedTask);
        Assert.True(result.HooksInstalled); Assert.Null(result.Error);
        Assert.All(wizard.Steps, step => Assert.NotEqual("AutoStart", step.ToString()));
    }
    private sealed class FakeRunner : IProcessRunner
    { public Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null) => Task.FromResult(new RuntimeProbeResult(fileName, 0, "ok", "")); }
}
