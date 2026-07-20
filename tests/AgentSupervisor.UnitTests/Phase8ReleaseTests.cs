using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class Phase8ReleaseTests
{
    [Fact]
    public async Task Update_restores_previous_binary_when_health_check_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentsupervisor-phase8-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var current = Path.Combine(root, "app.exe"); var staged = Path.Combine(root, "staged.exe");
            await File.WriteAllTextAsync(current, "old"); await File.WriteAllTextAsync(staged, "new");
            var ok = await new UpdateService(new FileBackupService()).StageAndSwapAsync(current, staged, Path.Combine(root, "backups"), () => Task.FromResult(false));
            Assert.False(ok); Assert.Equal("old", await File.ReadAllTextAsync(current));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Diagnostics_redacts_secrets_and_includes_version()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentsupervisor-phase8-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs); File.WriteAllText(Path.Combine(logs, "app.log"), "test log");
            var config = Path.Combine(root, "settings.json"); File.WriteAllText(config, "{\"token\":\"do-not-export\",\"theme\":\"dark\"}");
            var output = new DiagnosticsExportService().Export(Path.Combine(root, "diagnostics.zip"), logs, Path.Combine(root, "missing.db"), config, "1.0.0");
            using var zip = System.IO.Compression.ZipFile.OpenRead(output); var settings = new StreamReader(zip.GetEntry("settings.json")!.Open()).ReadToEnd();
            Assert.DoesNotContain("do-not-export", settings); Assert.Contains("[REDACTED]", settings); Assert.Contains("version.txt", zip.Entries.Select(x => x.FullName));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Log_rotation_moves_oversized_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentsupervisor-phase8-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "app.log"), new string('x', 32)); new LogRotationService().Rotate(root, 1); Assert.Empty(Directory.GetFiles(root, "*.log")); Assert.NotEmpty(Directory.GetFiles(root, "*.old")); }
        finally { Directory.Delete(root, true); }
    }
}
