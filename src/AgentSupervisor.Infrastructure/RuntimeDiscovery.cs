namespace AgentSupervisor.Infrastructure;

public sealed record RuntimeProbeResult(string Command, int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class RuntimeDiscovery
{
    private readonly IProcessRunner _runner;
    public RuntimeDiscovery(IProcessRunner? runner = null) => _runner = runner ?? new ProcessRunner();
    public Task<RuntimeProbeResult> ProbeAsync(string fileName, params string[] args) => _runner.RunAsync(fileName, args);
}
