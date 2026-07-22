using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentSupervisor.IntegrationTests;

// These tests intentionally never register a runtimeId that matches one of the real
// IClaudeRuntime instances the app wires up ("windows" / "wsl:Ubuntu") -- doing so would make
// the live TaskQueue background service actually spawn the real `claude` CLI installed on this
// machine. Using a made-up runtime id makes TaskQueue's "rt is null" branch fail the task
// immediately and safely, which still exercises the full submit -> queue -> persisted-outcome
// path end-to-end over HTTP.
//
// The app's own RuntimePoller hosted service still runs against whatever IClaudeRuntime
// instances are registered, though. Program.cs normally wires that up to the *real* Windows and
// WSL runtimes (spawning literal `claude`/`wsl.exe` processes every ~10s). That is fine on a dev
// machine with Claude Code and WSL actually installed and configured, but on CI (no `claude` CLI,
// and `wsl.exe` present but pointed at a distro name -- "Ubuntu" -- that doesn't exist) those
// calls can take many seconds to fail, which was intermittently starving/delaying the unrelated
// TaskQueue background service enough to blow this class's own polling deadlines (reproduced
// locally by hiding `claude` from PATH). Replace the real runtimes with instant fakes so this
// test host never depends on external processes being present at all.
public sealed class Phase5EndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    private sealed class InstantFakeRuntime : IClaudeRuntime
    {
        public required string RuntimeId { get; init; }
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ClaudeAgent>> ListAgentsAsync(bool includeAll, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClaudeAgent>>(Array.Empty<ClaudeAgent>());
        public Task<RuntimeCommandResult> GetDaemonStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("daemon", 0, "", ""));
        public Task<RuntimeCommandResult> GetAuthStatusAsync(CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("auth", 0, "", ""));
        public Task<RuntimeCommandResult> GetGitStatusAsync(string cwd, CancellationToken ct = default) => Task.FromResult(new RuntimeCommandResult("git", 0, "", ""));
        public Task<StartedProcessHandle> StartBackgroundAsync(string cwd, string prompt, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<StartedProcessHandle> StartBatchAsync(string cwd, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<StartedProcessHandle> ResumeBatchAsync(string cwd, string sessionId, string prompt, int maxTurns, decimal maxBudgetUsd, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<StartedProcessHandle> RespawnBackgroundAsync(string cwd, string? jobId, string prompt, CancellationToken ct = default) => Task.FromResult(new StartedProcessHandle("job", 1));
        public Task<int?> WaitForExitAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult<int?>(0);
        public Task StopAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public string GetStdoutExcerpt(string jobId) => "";
        public string GetStderrExcerpt(string jobId) => "";
    }

    public Phase5EndpointTests(WebApplicationFactory<Program> factory)
    {
        var customized = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClaudeRuntime>();
            services.AddSingleton<IClaudeRuntime>(new InstantFakeRuntime { RuntimeId = "windows" });
            services.AddSingleton<IClaudeRuntime>(new InstantFakeRuntime { RuntimeId = "wsl:Ubuntu" });
        }));
        _client = customized.CreateClient();
    }

    private async Task<(string RuntimeId, string ProjectId)> RegisterProjectAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var runtimeId = "it-runtime-" + suffix;
        var projectId = "it-project-" + suffix;
        var runtimeResponse = await _client.PostAsJsonAsync("/api/v1/runtimes", new RuntimeRecord(runtimeId, "windows", null, "claude"));
        Assert.Equal(HttpStatusCode.Created, runtimeResponse.StatusCode);
        var projectResponse = await _client.PostAsJsonAsync("/api/v1/projects", new ProjectRecord(projectId, runtimeId, @"C:\work\it-project", 1, "batch-print"));
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        return (runtimeId, projectId);
    }

    [Fact]
    public async Task Project_registration_is_listed_via_api()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var projects = await _client.GetFromJsonAsync<List<ProjectRecord>>("/api/v1/projects", Json);
        Assert.Contains(projects!, p => p.Id == projectId);
    }

    [Fact]
    public async Task Submitting_a_task_is_accepted_and_readable_by_id_and_in_list()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var submitResponse = await _client.PostAsJsonAsync("/api/v1/tasks", new { projectId, prompt = "say hi", mode = "batch-print" });
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var created = await submitResponse.Content.ReadFromJsonAsync<TaskRecord>(Json);
        Assert.NotNull(created);
        Assert.Equal(projectId, created!.ProjectId);
        Assert.Equal("queued", created.Status);

        var detailResponse = await _client.GetAsync($"/api/v1/tasks/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var list = await _client.GetFromJsonAsync<List<TaskRecord>>("/api/v1/tasks", Json);
        Assert.Contains(list!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task Unknown_task_id_returns_not_found()
    {
        var response = await _client.GetAsync("/api/v1/tasks/does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_endpoint_accepts_a_submitted_task()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var submitResponse = await _client.PostAsJsonAsync("/api/v1/tasks", new { projectId, prompt = "say hi", mode = "batch-print" });
        var created = await submitResponse.Content.ReadFromJsonAsync<TaskRecord>(Json);

        var cancelResponse = await _client.PostAsync($"/api/v1/tasks/{created!.Id}/cancel", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
    }

    [Fact]
    public async Task Task_against_an_unregistered_runtime_ends_up_visible_as_failed_history()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var submitResponse = await _client.PostAsJsonAsync("/api/v1/tasks", new { projectId, prompt = "say hi", mode = "batch-print" });
        var created = await submitResponse.Content.ReadFromJsonAsync<TaskRecord>(Json);

        string? status = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/tasks/{created!.Id}", Json);
            status = detail.GetProperty("task").GetProperty("status").GetString();
            if (status is "failed" or "succeeded") break;
            await Task.Delay(50);
        }

        Assert.Equal("failed", status);
    }
}
