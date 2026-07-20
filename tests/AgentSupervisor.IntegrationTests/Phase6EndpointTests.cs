using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentSupervisor.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentSupervisor.IntegrationTests;

public sealed class Phase6EndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public Phase6EndpointTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    private async Task<(string RuntimeId, string ProjectId)> RegisterProjectAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var runtimeId = "fake-runtime-" + suffix;
        var projectId = "phase6-project-" + suffix;
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/v1/runtimes", new RuntimeRecord(runtimeId, "windows", null, "claude"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/v1/projects", new ProjectRecord(projectId, runtimeId, @"C:\work\phase6", 1, "batch-print"))).StatusCode);
        return (runtimeId, projectId);
    }

    [Fact]
    public async Task Manual_stop_endpoint_sets_manual_stop()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var created = await (await _client.PostAsJsonAsync("/api/v1/tasks", new { projectId, prompt = "stop me", mode = "batch-print" }))
            .Content.ReadFromJsonAsync<TaskRecord>();

        var response = await _client.PostAsync($"/api/v1/tasks/{created!.Id}/manual-stop", new StringContent(""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/tasks/{created.Id}");
        Assert.True(detail.GetProperty("task").GetProperty("manualStop").GetBoolean());
    }

    [Fact]
    public async Task Task_detail_contains_recovery_history_for_fake_runtime()
    {
        var (_, projectId) = await RegisterProjectAsync();
        var created = await (await _client.PostAsJsonAsync("/api/v1/tasks", new { projectId, prompt = "history", mode = "batch-print" }))
            .Content.ReadFromJsonAsync<TaskRecord>();

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/tasks/{created!.Id}");

        Assert.True(detail.TryGetProperty("recoveryHistory", out var history));
        Assert.Equal(JsonValueKind.Array, history.ValueKind);
    }
}
