using System.Net;
using System.Text;
using AgentSupervisor.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentSupervisor.IntegrationTests;

public sealed class FixedHookTokens : IHookTokenProvider
{
    public string GetToken(string runtimeId) => "test-token";
}

// WithWebHostBuilder() returns a brand-new derived factory each call, and each one
// tries to intercept Program's entry point independently -- calling it once per test
// method (e.g. from this class's constructor) is unreliable in-process and produced
// "The entry point exited without ever building an IHost." on most runs. Overriding
// ConfigureWebHost once on a fixture subclass and sharing it via IClassFixture (which
// only builds the host once per test class) avoids the repeated interception.
public sealed class FixedTokenWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHookTokenProvider>();
            services.AddSingleton<IHookTokenProvider, FixedHookTokens>();
        });
}

public sealed class Phase3EndpointTests : IClassFixture<FixedTokenWebApplicationFactory>
{
    private readonly HttpClient _client;
    public Phase3EndpointTests(FixedTokenWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact] public async Task HookWithoutTokenIsUnauthorized() => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/hooks/events?runtime=windows", new StringContent("{}"))).StatusCode);
    [Fact] public async Task HookWithBadTokenIsUnauthorized() { _client.DefaultRequestHeaders.Authorization = new("Bearer", "bad"); Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/hooks/events?runtime=windows", new StringContent("{}"))).StatusCode); }
    [Fact] public async Task OversizedHookIsRejected() { _client.DefaultRequestHeaders.Authorization = new("Bearer", "test-token"); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/v1/hooks/events?runtime=windows", new StringContent(new string('x', 65537)))).StatusCode); }
    [Fact] public async Task SignalRHubIsMapped() => Assert.NotEqual(HttpStatusCode.NotFound, (await _client.GetAsync("/hubs/sessions/negotiate?negotiateVersion=1")).StatusCode);

    [Fact]
    public async Task HookWithValidTokenAndAllowedEventIsAccepted()
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        var body = new StringContent("{\"runtimeId\":\"windows\",\"sessionId\":\"s-1\",\"sourceEventId\":\"evt-1\",\"eventName\":\"Notification\",\"occurredAt\":\"2026-07-18T00:00:00Z\",\"payload\":{\"message\":\"ok\"}}", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/hooks/events?runtime=windows", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
