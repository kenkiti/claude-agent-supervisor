using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class GitRemoteUrlResolverTests
{
    [Fact]
    public async Task NormalizesSshGithubRemote()
    {
        var runner = new StubProcessRunner(new RuntimeProbeResult("git", 0, "git@github.com:owner/repo.git", ""));
        var resolver = new GitRemoteUrlResolver(runner);

        var result = await resolver.ResolveAsync("C:/repo", new RuntimeRecord("runtime", "windows", null));

        Assert.Equal("https://github.com/owner/repo", result);
    }

    [Fact]
    public async Task NormalizesHttpsGithubRemote()
    {
        var runner = new StubProcessRunner(new RuntimeProbeResult("git", 0, "https://github.com/owner/repo.git", ""));
        var resolver = new GitRemoteUrlResolver(runner);

        var result = await resolver.ResolveAsync("C:/repo", new RuntimeRecord("runtime", "windows", null));

        Assert.Equal("https://github.com/owner/repo", result);
    }

    [Fact]
    public async Task ReturnsNullForFailingProcess()
    {
        var runner = new StubProcessRunner(new RuntimeProbeResult("git", 1, "", "not a repository"));
        var resolver = new GitRemoteUrlResolver(runner);

        var result = await resolver.ResolveAsync("C:/repo", new RuntimeRecord("runtime", "windows", null));

        Assert.Null(result);
    }

    [Fact]
    public async Task UsesGitForWindowsRuntime()
    {
        var runner = new StubProcessRunner(new RuntimeProbeResult("git", 0, "https://example.com/owner/repo.git", ""));
        var resolver = new GitRemoteUrlResolver(runner);

        await resolver.ResolveAsync("C:/repo", new RuntimeRecord("runtime", "windows", null));

        Assert.Equal("git", runner.FileName);
        Assert.Equal(new[] { "-C", "C:/repo", "remote", "get-url", "origin" }, runner.Arguments);
    }

    [Fact]
    public async Task UsesWslForWslRuntime()
    {
        var runner = new StubProcessRunner(new RuntimeProbeResult("wsl.exe", 0, "https://example.com/owner/repo.git", ""));
        var resolver = new GitRemoteUrlResolver(runner);

        await resolver.ResolveAsync("/repo", new RuntimeRecord("runtime", "wsl", "Ubuntu"));

        Assert.Equal("wsl.exe", runner.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu", "--", "git", "-C", "/repo", "remote", "get-url", "origin" }, runner.Arguments);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly RuntimeProbeResult _result;
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public StubProcessRunner(RuntimeProbeResult result) => _result = result;

        public Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null)
        {
            FileName = fileName;
            Arguments = args;
            return Task.FromResult(_result);
        }
    }
}
