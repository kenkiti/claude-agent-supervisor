using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentSupervisor.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentSupervisor.IntegrationTests;

// These tests intentionally never register a runtimeId that matches one of the real
// IClaudeRuntime instances the app wires up ("windows" / "wsl:Ubuntu") -- doing so would make
// the live TaskQueue background service actually spawn the real `claude` CLI installed on this
// machine. Using a made-up runtime id makes TaskQueue's "rt is null" branch fail the task
// immediately and safely, which still exercises the full submit -> queue -> persisted-outcome
// path end-to-end over HTTP.
public sealed class Phase5EndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    public Phase5EndpointTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

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
