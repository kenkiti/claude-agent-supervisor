using System.Diagnostics;
using System.Text.Json;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class Phase3UnitTests
{
    [Fact]
    public void SettingsMergeIsPreservingDryRunAndIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{\"custom\":{\"keep\":true},\"hooks\":{\"Other\":[]}}");
            var merger = new ClaudeSettingsHookMerger(path);
            var before = File.ReadAllText(path);
            merger.Merge("http://127.0.0.1/hook", "test-token", true);
            Assert.Equal(before, File.ReadAllText(path));
            merger.Merge("http://127.0.0.1/hook", "test-token");
            merger.Merge("http://127.0.0.1/hook", "test-token");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(json.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean());
            var hooks = json.RootElement.GetProperty("hooks");
            var notification = hooks.GetProperty("Notification")[0].GetProperty("hooks")[0];
            Assert.Equal("command", notification.GetProperty("type").GetString());
            Assert.Contains(Process.GetCurrentProcess().MainModule!.FileName!, notification.GetProperty("command").GetString());
            Assert.Contains("hook-forward --runtime windows", notification.GetProperty("command").GetString());
            Assert.Equal(ClaudeSettingsHookMerger.PermissionRequestTimeoutSeconds, hooks.GetProperty("PermissionRequest")[0].GetProperty("hooks")[0].GetProperty("timeout").GetInt32());
            Assert.Equal("AskUserQuestion", hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
            Assert.Single(hooks.GetProperty("Notification").EnumerateArray());
            Assert.False(hooks.GetProperty("Notification")[0].TryGetProperty("matcher", out _));
            merger.Uninstall();
            using var restored = JsonDocument.Parse(File.ReadAllText(path));
            var restoredHooks = restored.RootElement.GetProperty("hooks");
            Assert.False(restoredHooks.TryGetProperty("Notification", out _));
            Assert.False(restoredHooks.TryGetProperty("PermissionRequest", out _));
            Assert.False(restoredHooks.TryGetProperty("PreToolUse", out _));
            Assert.True(restored.RootElement.GetProperty("hooks").TryGetProperty("Other", out _));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RedactMasksSecretKeysAndKeepsOtherValues()
    {
        using var json = JsonDocument.Parse("{\"token\":\"x\",\"secret\":\"y\",\"password\":\"z\",\"authorization\":\"a\",\"api_key\":\"b\",\"apikey\":\"c\",\"name\":\"kept\"}");
        var output = JsonDocument.Parse(HookSecurity.Redact(json.RootElement)).RootElement;
        foreach (var key in new[] { "token", "secret", "password", "authorization", "api_key", "apikey" }) Assert.Equal("[REDACTED]", output.GetProperty(key).GetString());
        Assert.Equal("kept", output.GetProperty("name").GetString());
    }

    [Fact]
    public void DuplicateSourceEventIsIgnored()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var store = new HookEventStore(cs);
            using var payload = JsonDocument.Parse("{\"message\":\"ok\"}");
            var e = new HookEvent("windows", "session-1", "event-1", "Notification", DateTimeOffset.UtcNow, payload.RootElement.Clone());
            var text = HookSecurity.Redact(e.Payload);
            Assert.True(store.Save(e, "test", text, HookEventStore.Hash(text)));
            Assert.False(store.Save(e, "test", text, HookEventStore.Hash(text)));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void UninstallRemovesHooksEvenWhenExecutablePathDiffersFromInstall()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{\"theme\":\"dark\"}");
            new ClaudeSettingsHookMerger(path, "wsl", @"C:\old\location\AgentSupervisor.App.exe").Merge("http://127.0.0.1/hook", null);
            using (var installed = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Single(installed.RootElement.GetProperty("hooks").GetProperty("Notification").EnumerateArray());
            new ClaudeSettingsHookMerger(path, "wsl", @"C:\new\location\AgentSupervisor.App.exe").Uninstall();
            using var restored = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(restored.RootElement.GetProperty("hooks").TryGetProperty("Notification", out _));
            Assert.True(restored.RootElement.GetProperty("theme").GetString() == "dark");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReinstallWithDifferentExecutablePathUpdatesInPlaceInsteadOfDuplicating()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            new ClaudeSettingsHookMerger(path, "wsl", @"C:\old\location\AgentSupervisor.App.exe").Merge("http://127.0.0.1/hook", null);
            new ClaudeSettingsHookMerger(path, "wsl", @"C:\new\location\AgentSupervisor.App.exe").Merge("http://127.0.0.1/hook", null);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var notifications = json.RootElement.GetProperty("hooks").GetProperty("Notification");
            Assert.Single(notifications.EnumerateArray());
            var command = notifications[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
            Assert.Contains(@"C:\new\location\AgentSupervisor.App.exe", command);
            Assert.DoesNotContain(@"C:\old\location\AgentSupervisor.App.exe", command);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
