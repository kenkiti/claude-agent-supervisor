using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentSupervisor.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentSupervisor.IntegrationTests;

public sealed class Phase7EndpointTests : IClassFixture<FixedTokenWebApplicationFactory>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Phase7EndpointTests(FixedTokenWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Questions_require_a_valid_hook_token_on_create()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/questions", new { runtimeId = "phase7-auth-" + Guid.NewGuid().ToString("N"), sessionId = "s", questionsJson = "[]" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Questions_can_be_created_listed_read_and_answered_once()
    {
        var runtime = "phase7-" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/questions") { Content = JsonContent.Create(new { runtimeId = runtime, sessionId = "session-1", questionsJson = "[{\"question\":\"Color?\"}]" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        var createdResponse = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = created.GetProperty("id").GetString()!;

        var list = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/questions", Json);
        Assert.Contains(list!, q => q.GetProperty("id").GetString() == id && q.GetProperty("status").GetString() == "pending");
        Assert.Equal("pending", (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/questions/{id}", Json)).GetProperty("status").GetString());

        var answer = await _client.PostAsJsonAsync($"/api/v1/questions/{id}/answer", new { answersJson = "{\"Color?\":\"Red\"}" });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var answered = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/questions/{id}", Json);
        Assert.Equal("answered", answered.GetProperty("status").GetString());
        Assert.Equal("{\"Color?\":\"Red\"}", answered.GetProperty("answersJson").GetString());
        var roundTripped = JsonSerializer.Deserialize<PendingQuestionRecord>(answered.GetRawText(), Json);
        Assert.Equal(id, roundTripped!.Id);
        Assert.Equal("answered", roundTripped.Status);
        Assert.Equal("{\"Color?\":\"Red\"}", roundTripped.AnswersJson);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/v1/questions/{id}/answer", new { answersJson = "{}" })).StatusCode);
    }

    [Fact]
    public async Task Unknown_questions_return_not_found()
    {
        var id = "missing-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/questions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/v1/questions/{id}/answer", new { answersJson = "{}" })).StatusCode);
    }
}
