using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.ContractTests;

public sealed class AgentViewContractTests
{
    [Fact]
    public void Windows_fixture_is_parsed()
    {
        var json = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "fixtures", "claude-agent-view", "windows", "agents.json"));
        var agents = ClaudeAgentParser.Parse(json);
        Assert.Single(agents); Assert.Equal("7a9c8689-7d26-4639-a744-1acbe57776b4", agents[0].SessionId);
    }

    [Fact]
    public void Bare_array_shape_from_real_cli_is_parsed()
    {
        // `claude agents --json --all` itself returns a bare JSON array with no
        // {"result": [...]} envelope. This regresses a real bug found by running the
        // published exe against the live CLI: JsonElement.TryGetProperty throws
        // (instead of returning false) when called on a non-Object root element.
        var agents = ClaudeAgentParser.Parse("[{\"pid\":22568,\"sessionId\":\"live-session\",\"status\":\"busy\"}]");
        Assert.Single(agents);
        Assert.Equal("live-session", agents[0].SessionId);
    }

    [Fact]
    public void Wsl_shape_fixture_is_parsed()
    {
        var json = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "fixtures", "claude-agent-view", "wsl", "agents.json"));
        var agents = ClaudeAgentParser.Parse(json);
        Assert.Single(agents); Assert.Equal("wsl-sample-1", agents[0].SessionId);
    }
}
