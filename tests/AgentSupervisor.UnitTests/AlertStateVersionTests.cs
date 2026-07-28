using System.Text.Json;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class AlertStateVersionTests
{
    [Fact]
    public void StableStateVersion_ReturnsZeroForMissingKey()
    {
        Assert.Equal(0, AlertEngine.StableStateVersion(null));
        Assert.Equal(0, AlertEngine.StableStateVersion(null));
        Assert.Equal(0, AlertEngine.StableStateVersion(""));
    }

    [Fact]
    public void StableStateVersion_IsStableDistinctAndNonNegative()
    {
        var first = AlertEngine.StableStateVersion("evt-1");
        var same = AlertEngine.StableStateVersion("evt-1");
        var different = AlertEngine.StableStateVersion("evt-2");

        Assert.Equal(first, same);
        Assert.NotEqual(first, different);
        Assert.True(first >= 0);
        Assert.True(different >= 0);
    }

    [Fact]
    public void FromHook_UsesStableSourceEventVersion()
    {
        using var document = JsonDocument.Parse("{}");
        var payload = document.RootElement.Clone();
        var engine = new AlertEngine();
        var firstHook = new HookEvent("runtime", "session", "evt-1", "Stop", null, payload);
        var secondHook = new HookEvent("runtime", "session", "evt-2", "Stop", null, payload);
        var retryHook = new HookEvent("runtime", "session", "evt-1", "Stop", null, payload);

        var first = engine.FromHook(firstHook, "windows");
        var second = engine.FromHook(secondHook, "windows");
        var retry = engine.FromHook(retryHook, "windows");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(retry);
        Assert.NotEqual(0, first!.StateVersion);
        Assert.NotEqual(first.StateVersion, second!.StateVersion);
        Assert.Equal(first.StateVersion, retry!.StateVersion);
    }

    [Fact]
    public void QuestionPending_CarriesResolvedProjectName()
    {
        var question = new PendingQuestionRecord("question", "windows", "session", "[{\"question\":\"continue?\"}]", "pending", null, DateTimeOffset.UtcNow, null);

        var candidate = new AlertEngine().QuestionPending(question, "my-app");

        Assert.Equal("my-app", candidate!.Project);
    }
}
