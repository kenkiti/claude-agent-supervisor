using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class RuntimeTests
{
    [Fact]
    public void Normalizer_maps_busy_agent()
    {
        var value = SessionNormalizer.Normalize(new ClaudeAgent(1, "/p", "interactive", null, "s", "n", "busy"), "windows", DateTimeOffset.UtcNow);
        Assert.Equal("active", value.ActivityState);
        Assert.Equal("claude-agent-view", value.Source);
    }

    [Fact]
    public void Normalizer_maps_finished_background_session_to_finished_lifecycle()
    {
        var value = SessionNormalizer.Normalize(new ClaudeAgent(null, "/p", "background", null, "s", "n", null, "done"), "wsl", DateTimeOffset.UtcNow);
        Assert.Equal("finished", value.LifecycleState);
    }

    [Fact]
    public void Normalizer_maps_explicitly_stopped_session_to_stopped_lifecycle()
    {
        var value = SessionNormalizer.Normalize(new ClaudeAgent(null, "/p", "background", null, "s", "n", null, "stopped"), "wsl", DateTimeOffset.UtcNow);
        Assert.Equal("stopped", value.LifecycleState);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var value = ClaudeAgentParser.Parse("{\"result\":[{\"sessionId\":\"x\",\"futureField\":true}]}");
        Assert.Single(value);
    }

    [Fact]
    public async Task Invalid_json_is_reported_by_runtime_as_empty_agents()
    {
        var runtime = new WindowsClaudeRuntime(new FakeRunner(new RuntimeProbeResult("claude", 0, "not-json", "")));
        Assert.Empty(await runtime.ListAgentsAsync(true));
    }

    [Fact]
    public async Task Non_zero_exit_is_reported_without_throwing()
    {
        var runtime = new WindowsClaudeRuntime(new FakeRunner(new RuntimeProbeResult("claude", 7, "", "failed")));
        var status = await runtime.GetDaemonStatusAsync();
        Assert.Equal(7, status.ExitCode);
        Assert.False(status.Succeeded);
    }

    [Fact]
    public async Task Wsl_runtime_invokes_resolved_absolute_claude_path()
    {
        var fake = new FakeRunner(new RuntimeProbeResult("claude", 0, "[]", ""));
        var runtime = new WslClaudeRuntime(fake, "Ubuntu-24.04", "/home/user/.local/bin/claude");
        await runtime.HealthCheckAsync();
        Assert.Equal("wsl.exe", fake.LastFileName);
        Assert.Equal(new[] { "-d", "Ubuntu-24.04", "--", "/home/user/.local/bin/claude", "--version" }, fake.LastArgs);
    }

    [Fact]
    public async Task Wsl_runtime_defaults_to_bare_claude_for_backward_compatibility()
    {
        var fake = new FakeRunner(new RuntimeProbeResult("claude", 0, "[]", ""));
        var runtime = new WslClaudeRuntime(fake, "Ubuntu-24.04");
        await runtime.HealthCheckAsync();
        Assert.Equal(new[] { "-d", "Ubuntu-24.04", "--", "claude", "--version" }, fake.LastArgs);
    }

    [Fact]
    public async Task Wsl_runtime_uses_wsl_cd_flag_instead_of_windows_working_directory()
    {
        var fake = new FakeRunner(new RuntimeProbeResult("claude", 0, "", ""));
        var runtime = new WslClaudeRuntime(fake, "Ubuntu-24.04", "/home/user/.local/bin/claude");
        await runtime.GetGitStatusAsync("/home/user/Github/sample-app");
        Assert.Equal("wsl.exe", fake.LastFileName);
        Assert.Equal(new[] { "--cd", "/home/user/Github/sample-app", "-d", "Ubuntu-24.04", "--", "/home/user/.local/bin/claude", "git", "status", "--porcelain" }, fake.LastArgs);
        Assert.Equal(Environment.CurrentDirectory, fake.LastCwd);
    }

    private sealed class FakeRunner(RuntimeProbeResult result) : IProcessRunner
    {
        public string? LastFileName;
        public IReadOnlyList<string>? LastArgs;
        public string? LastCwd;
        public Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null)
        {
            LastFileName = fileName;
            LastArgs = args;
            LastCwd = cwd;
            return Task.FromResult(result);
        }
    }
}
