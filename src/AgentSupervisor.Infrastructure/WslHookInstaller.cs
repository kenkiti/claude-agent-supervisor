namespace AgentSupervisor.Infrastructure;

public sealed class WslHookInstaller
{
    private readonly IProcessRunner _runner;
    public WslHookInstaller(IProcessRunner runner) => _runner = runner;
    public async Task<string?> ResolveSettingsPathAsync(string distro = "Ubuntu")
    {
        var home = await _runner.RunAsync("wsl.exe", new[] { "-d", distro, "--", "printenv", "HOME" });
        if (!home.Succeeded) return null; var value = home.Output.Trim(); if (string.IsNullOrWhiteSpace(value) || value.Contains(' ')) return null;
        var unix = value.TrimStart('/').Replace('/', '\\'); return $"\\\\wsl$\\{distro}\\{unix}\\.claude\\settings.json";
    }
    public async Task<bool> InstallAsync(string executable, string distro = "Ubuntu", bool dryRun = false)
    { var path = await ResolveSettingsPathAsync(distro); if (path is null) return false; new ClaudeSettingsHookMerger(path, "wsl", ToWslPath(executable)).Merge("http://127.0.0.1:8700/api/v1/hooks/events", null, dryRun); return true; }
    public async Task<bool> UninstallAsync(string distro = "Ubuntu", bool dryRun = false)
    { var path = await ResolveSettingsPathAsync(distro); if (path is null) return false; new ClaudeSettingsHookMerger(path, "wsl", null).Uninstall(dryRun); return true; }
    private static string ToWslPath(string windowsPath)
    {
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var drive = char.ToLowerInvariant(windowsPath[0]);
            var rest = windowsPath.Substring(2).Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }
        return windowsPath;
    }
}
