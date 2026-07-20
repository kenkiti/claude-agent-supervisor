using System.Net;
using AgentSupervisor.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
namespace AgentSupervisor.IntegrationTests;

public class BasicIntegrationTests
{
    [Fact]
    public void Migration_is_idempotent()
    {
        var db = Path.GetTempFileName();
        var r = new SqliteMigrationRunner();
        r.Migrate($"Data Source={db}");
        r.Migrate($"Data Source={db}");
        File.Delete(db);
    }
}

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_api_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
