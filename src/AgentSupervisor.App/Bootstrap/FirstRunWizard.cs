using AgentSupervisor.Infrastructure;
namespace AgentSupervisor.App.Bootstrap;

public enum WizardStep { Welcome, RuntimeDiscovery, Storage, Hooks, Complete }
public sealed record WizardResult(IReadOnlyList<RuntimeProbeResult> Runtimes, bool HooksInstalled, string? Error = null);
public sealed class FirstRunWizard
{
    public IReadOnlyList<WizardStep> Steps { get; } = Enum.GetValues<WizardStep>();
    public async Task<WizardResult> RunAsync(RuntimeDiscovery discovery, IEnumerable<(string File, string[] Args)> probes, Func<Task> installHooks)
    {
        var results = new List<RuntimeProbeResult>(); foreach (var p in probes) { try { results.Add(await discovery.ProbeAsync(p.File, p.Args)); } catch (Exception ex) { results.Add(new(p.File, -1, "", ex.Message)); } }
        try { await installHooks(); } catch (Exception ex) { return new(results, false, ex.Message); }
        return new(results, true);
    }
}
