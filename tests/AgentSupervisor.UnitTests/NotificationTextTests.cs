using System.Text.Json;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class NotificationTextTests
{
    [Fact]
    public void ExtractMessage_reads_last_assistant_message_from_stop_hook()
    {
        var candidate = new AlertCandidate(
            "task-completed",
            "info",
            "runtime",
            null,
            0,
            """{"hook_event_name":"Stop","last_assistant_message":"some completion text","stop_hook_active":false}""",
            new[] { "windows" });

        Assert.Equal("some completion text", NotificationText.ExtractMessage(candidate));
    }
    [Fact]
    public void Label_UsesJapaneseLabelAndFallsBackToRawId()
    {
        Assert.Equal("タスクが完了しました", NotificationText.Label("task-completed"));
        Assert.Equal("unknown-rule", NotificationText.Label("unknown-rule"));
    }

    [Fact]
    public void ExtractMessage_ReturnsObjectMessage()
    {
        var candidate = Candidate(JsonSerializer.Serialize(new { message = "hello" }));

        Assert.Equal("hello", NotificationText.ExtractMessage(candidate));
    }

    [Fact]
    public void ExtractMessage_ReturnsFirstArrayQuestion()
    {
        var candidate = Candidate(JsonSerializer.Serialize(new[] { new { question = "first" }, new { question = "second" } }));

        Assert.Equal("first", NotificationText.ExtractMessage(candidate));
    }

    [Fact]
    public void ExtractMessage_ReturnsNullForInvalidOrWrongShape()
    {
        Assert.Null(NotificationText.ExtractMessage(Candidate("not json")));
        Assert.Null(NotificationText.ExtractMessage(Candidate("{}")));
    }

    [Fact]
    public void ExtractMessage_TruncatesLongMessage()
    {
        var candidate = Candidate(JsonSerializer.Serialize(new { message = new string('x', 500) }));

        var result = NotificationText.ExtractMessage(candidate);

        Assert.NotNull(result);
        Assert.Equal(401, result!.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Compose_ContainsSeverityLabelAndMessageOnItsOwnLine()
    {
        var candidate = new AlertCandidate("task-completed", "info", "runtime", null, 0, JsonSerializer.Serialize(new { message = "done" }), new[] { "windows" });

        var result = NotificationText.Compose(candidate);

        Assert.Contains("[info]", result);
        Assert.Contains("タスクが完了しました", result);
        Assert.Contains("(task-completed)", result);
        Assert.Contains(Environment.NewLine + "done", result);
    }

    [Fact]
    public void Compose_UsesUnknownRuleIdOnlyOnce()
    {
        var candidate = new AlertCandidate("my-custom-rule", "info", "runtime", null, 0, "{}", new[] { "windows" });

        var result = NotificationText.Compose(candidate);

        Assert.Equal(1, result.Split("my-custom-rule", StringSplitOptions.None).Length - 1);
    }

    private static AlertCandidate Candidate(string payload) =>
        new("test", "info", "runtime", null, 0, payload, new[] { "windows" });
}
