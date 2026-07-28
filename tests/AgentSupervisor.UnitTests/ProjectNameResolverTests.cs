using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class ProjectNameResolverTests
{
    [Fact]
    public async Task Resolves_github_repository_name_from_registered_runtime()
    {
        using var fixture = new Fixture(new RuntimeProbeResult("git", 0, "git@github.com:owner/repository.git", ""));
        fixture.Store.AddRuntime(new RuntimeRecord("windows", "windows", null));

        var result = await fixture.Resolver.ResolveAsync(@"C:\work\folder", "windows");

        Assert.Equal("repository", result);
        Assert.Equal(1, fixture.Runner.Calls);
    }

    [Fact]
    public async Task Falls_back_to_folder_name_when_git_resolution_fails()
    {
        using var fixture = new Fixture(new RuntimeProbeResult("git", 1, "", "not a repository"));
        fixture.Store.AddRuntime(new RuntimeRecord("windows", "windows", null));

        var result = await fixture.Resolver.ResolveAsync(@"C:\work\folder\", "windows");

        Assert.Equal("folder", result);
        Assert.Equal(1, fixture.Runner.Calls);
    }

    [Fact]
    public async Task Falls_back_to_folder_name_when_runtime_is_not_registered()
    {
        using var fixture = new Fixture(new RuntimeProbeResult("git", 0, "https://github.com/owner/repository.git", ""));

        var result = await fixture.Resolver.ResolveAsync("/home/user/folder/", "wsl:Ubuntu");

        Assert.Equal("folder", result);
        Assert.Equal(0, fixture.Runner.Calls);
    }

    [Fact]
    public async Task Caches_the_name_and_does_not_invoke_git_again_for_the_same_cwd()
    {
        using var fixture = new Fixture(new RuntimeProbeResult("git", 0, "https://github.com/owner/repository.git", ""));
        fixture.Store.AddRuntime(new RuntimeRecord("windows", "windows", null));

        var first = await fixture.Resolver.ResolveAsync("C:/work/folder", "windows");
        var second = await fixture.Resolver.ResolveAsync("C:/work/folder", "windows");

        Assert.Equal("repository", first);
        Assert.Equal("repository", second);
        Assert.Equal(1, fixture.Runner.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        public Fixture(RuntimeProbeResult result)
        {
            var connectionString = "Data Source=" + _file;
            new SqliteMigrationRunner().Migrate(connectionString);
            Store = new ProjectRegistryStore(connectionString);
            Runner = new StubProcessRunner(result);
            Resolver = new ProjectNameResolver(new GitRemoteUrlResolver(Runner), Store);
        }

        public ProjectRegistryStore Store { get; }
        public StubProcessRunner Runner { get; }
        public ProjectNameResolver Resolver { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_file)) File.Delete(_file);
        }
    }

    public sealed class StubProcessRunner : IProcessRunner
    {
        private readonly RuntimeProbeResult _result;
        public int Calls { get; private set; }

        public StubProcessRunner(RuntimeProbeResult result) => _result = result;

        public Task<RuntimeProbeResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken = default, string? cwd = null)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
