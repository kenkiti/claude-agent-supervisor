using Microsoft.Playwright;
using Xunit;
namespace AgentSupervisor.UiTests;

public sealed class DashboardSmokeTests
{
    [Theory, InlineData("/"), InlineData("/Projects"), InlineData("/Tasks"), InlineData("/Questions")]
    public async Task Dashboard_page_is_reachable(string page)
    {
        if (Environment.GetEnvironmentVariable("AGENTSUPERVISOR_UI_BASE_URL") is not { Length: > 0 } baseUrl) return;
        using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var response = await (await browser.NewPageAsync()).GotoAsync(baseUrl.TrimEnd('/') + page);
        Assert.NotNull(response); Assert.True(response!.Ok, $"{page}: {response.Status}");
    }
}
