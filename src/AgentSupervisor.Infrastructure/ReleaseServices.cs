using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSupervisor.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace AgentSupervisor.Infrastructure;

public interface ITaskScheduler { Task<bool> RegisterAsync(string executable, bool dryRun = false); Task<bool> UnregisterAsync(bool dryRun = false); }
public sealed class WindowsTaskScheduler : ITaskScheduler
{
    private readonly IProcessRunner _runner; private const string Name = "AgentSupervisor";
    public WindowsTaskScheduler(IProcessRunner runner) => _runner = runner;
    public Task<bool> RegisterAsync(string executable, bool dryRun = false) => Run(dryRun, new[] { "/Create", "/TN", Name, "/TR", executable, "/SC", "ONLOGON", "/F" });
    public Task<bool> UnregisterAsync(bool dryRun = false) => Run(dryRun, new[] { "/Delete", "/TN", Name, "/F" });
    private async Task<bool> Run(bool dryRun, IReadOnlyList<string> args) { if (dryRun || !OperatingSystem.IsWindows()) return true; var result = await _runner.RunAsync("schtasks.exe", args); return result.Succeeded; }
}

public sealed class FileBackupService
{
    public string? Backup(string path, string directory)
    { if (!File.Exists(path)) return null; Directory.CreateDirectory(directory); var target = Path.Combine(directory, Path.GetFileName(path) + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak"); File.Copy(path, target, false); return target; }
    public void Restore(string backup, string target) { File.Copy(backup, target, true); }
}

public sealed class UpdateService
{
    private readonly FileBackupService _backups;
    public UpdateService(FileBackupService backups) => _backups = backups;
    public async Task<bool> StageAndSwapAsync(string current, string staged, string backupDirectory, Func<Task<bool>> healthCheck, bool dryRun = false)
    {
        if (dryRun) return true; if (!File.Exists(staged)) throw new FileNotFoundException("Staged update was not found", staged);
        var backup = _backups.Backup(current, backupDirectory);
        var displaced = current + "." + Guid.NewGuid().ToString("N") + ".running";
        File.Move(current, displaced, true);
        File.Copy(staged, current, true);
        try { File.Delete(displaced); } catch { }
        if (await healthCheck()) return true; if (backup is null) return false; _backups.Restore(backup, current); return false;
    }
    public void Restore(string backup, string current, bool dryRun = false) { if (!dryRun) _backups.Restore(backup, current); }
}

public sealed class LogRotationService
{
    public void Rotate(string directory, long maxBytes = 5 * 1024 * 1024, TimeSpan? maxAge = null)
    { if (!Directory.Exists(directory)) return; foreach (var file in Directory.EnumerateFiles(directory, "*.log")) { var info = new FileInfo(file); if (info.Length > maxBytes || info.LastWriteTimeUtc < DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(14))) File.Move(file, file + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".old", true); } }
}

public sealed class FileLogWriter
{
    private readonly string _directory;
    public FileLogWriter(string directory) { _directory = directory; Directory.CreateDirectory(directory); }
    public void Write(string level, string message, Exception? error = null)
    { var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{(error is null ? "" : " " + error.Message)}{Environment.NewLine}"; File.AppendAllText(Path.Combine(_directory, "agentsupervisor.log"), line); }
}

public sealed class LogRotationWorker : BackgroundService
{
    private readonly LogRotationService _rotation; private readonly LocalAppDataLayout _layout;
    public LogRotationWorker(LogRotationService rotation, AgentSupervisor.Core.LocalAppDataLayout layout) { _rotation = rotation; _layout = layout; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    { _rotation.Rotate(_layout.Logs); using var timer = new PeriodicTimer(TimeSpan.FromDays(1)); while (await timer.WaitForNextTickAsync(stoppingToken)) _rotation.Rotate(_layout.Logs); }
}

public sealed class DiagnosticsExportService
{
    public string Export(string target, string logs, string database, string settings, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); if (File.Exists(target)) File.Delete(target);
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        AddDirectory(zip, logs, "logs", target); AddDatabaseSnapshot(zip, database, "database.snapshot");
        if (File.Exists(settings)) { using var doc = JsonDocument.Parse(File.ReadAllText(settings)); var redacted = HookSecurity.Redact(doc.RootElement); AddText(zip, "settings.json", redacted); }
        AddText(zip, "version.txt", version); return target;
    }
    private static void AddDirectory(ZipArchive z, string dir, string prefix, string target) { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) AddFile(z, f, prefix + "/" + Path.GetFileName(f)); }
    private static void AddFile(ZipArchive z, string path, string name) { if (!File.Exists(path)) return; z.CreateEntryFromFile(path, name); }
    private static void AddDatabaseSnapshot(ZipArchive z, string path, string name)
    {
        if (!File.Exists(path)) return; var temp = Path.Combine(Path.GetTempPath(), "agentsupervisor-diagnostics-" + Guid.NewGuid().ToString("N") + ".db");
        try { File.Copy(path, temp); using (var c = new SqliteConnection($"Data Source={temp}")) { c.Open(); foreach (var table in new[] { "settings", "session_events", "notification_outbox" }) { using var cmd = c.CreateCommand(); cmd.CommandText = table == "settings" ? "UPDATE settings SET value='[REDACTED]'" : $"UPDATE {table} SET payload_json='[REDACTED]'"; try { cmd.ExecuteNonQuery(); } catch (SqliteException) { } } c.Close(); SqliteConnection.ClearPool(c); } AddFile(z, temp, name); }
        finally { try { File.Delete(temp); } catch { } }
    }
    private static void AddText(ZipArchive z, string name, string value) { using var w = new StreamWriter(z.CreateEntry(name).Open(), Encoding.UTF8); w.Write(value); }
}

public static class Sha256
{ public static string Compute(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); } }
