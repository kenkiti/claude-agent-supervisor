namespace AgentSupervisor.Core;

public sealed class LocalAppDataLayout
{
    public string Root { get; }
    public string Bin => Path.Combine(Root, "bin");
    public string Config => Path.Combine(Root, "config");
    public string Data => Path.Combine(Root, "data");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string Runtime => Path.Combine(Root, "runtime");
    public LocalAppDataLayout(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSupervisor");
    public void EnsureDirectories() { foreach (var path in new[] { Root, Bin, Config, Data, Logs, Backups, Runtime }) Directory.CreateDirectory(path); }
}
