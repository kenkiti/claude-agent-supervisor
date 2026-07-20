using System.Text.Json;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class Phase7UnitTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    [Fact]
    public void PendingQuestionStore_round_trip_and_rejects_second_answer()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var store = new PendingQuestionStore(cs);
            var created = store.Create("test-runtime", "session-1", "[{\"question\":\"Color?\"}]");
            Assert.Equal("pending", created.Status);
            Assert.Null(created.AnswersJson);
            Assert.Null(created.AnsweredAt);
            Assert.Equal(created.Id, store.Get(created.Id)!.Id);
            Assert.True(store.Answer(created.Id, "{\"Color?\":\"Red\"}"));
            Assert.False(store.Answer(created.Id, "{\"Color?\":\"Blue\"}"));
            var answered = store.Get(created.Id)!;
            Assert.Equal("answered", answered.Status);
            Assert.Equal("{\"Color?\":\"Red\"}", answered.AnswersJson);
            Assert.NotNull(answered.AnsweredAt);
            Assert.Contains(store.All(), q => q.Id == created.Id && q.Status == "answered");
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void HookPayloadTranslator_translates_real_claude_payload_and_hashes_without_tool_use_id()
    {
        using var document = JsonDocument.Parse("""
        {"session_id":"9cadd02f-ab78-4152-9162-ee913d401845","transcript_path":"C:\\\\...\\\\9cadd02f.jsonl","cwd":"C:\\\\...","prompt_id":"f4d651bc-0000-0000-0000-000000000000","permission_mode":"default","effort":{"level":"high"},"hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"What is your favorite color?","header":"Fav color","options":[{"label":"Red","description":"Red"},{"label":"Blue","description":"Blue"}],"multiSelect":false}]}}
        """);
        var first = HookPayloadTranslator.Translate(document.RootElement, "test-runtime");
        var second = HookPayloadTranslator.Translate(document.RootElement, "test-runtime");
        Assert.Equal("PermissionRequest", first.EventName);
        Assert.Equal("9cadd02f-ab78-4152-9162-ee913d401845", first.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(first.SourceEventId));
        Assert.Equal(first.SourceEventId, second.SourceEventId);
        Assert.True(HookSecurity.IsAllowed(first.EventName));
    }
}
