using AgentSupervisor.Infrastructure;
namespace AgentSupervisor.App.Bootstrap;

public enum WizardStep { Welcome, RuntimeDiscovery, Storage, Hooks, AutoStart, Complete }
public sealed record WizardResult(IReadOnlyList<RuntimeProbeResult> Runtimes, bool HooksInstalled, bool AutoStartEnabled, string? Error = null);
public sealed class FirstRunWizard
{
    public IReadOnlyList<WizardStep> Steps { get; } = Enum.GetValues<WizardStep>();
    public async Task<WizardResult> RunAsync(RuntimeDiscovery discovery, IEnumerable<(string File, string[] Args)> probes, Func<Task> installHooks, ITaskScheduler scheduler, string executable, bool dryRun = false)
    {
        var results = new List<RuntimeProbeResult>(); foreach (var p in probes) { try { results.Add(await discovery.ProbeAsync(p.File, p.Args)); } catch (Exception ex) { results.Add(new(p.File, -1, "", ex.Message)); } }
        try { await installHooks(); } catch (Exception ex) { return new(results, false, false, ex.Message); }
        try { var registered = await scheduler.RegisterAsync(executable, dryRun); return new(results, true, registered, registered ? null : "Task Scheduler registration failed"); } catch (Exception ex) { return new(results, true, false, ex.Message); }
    }
}
