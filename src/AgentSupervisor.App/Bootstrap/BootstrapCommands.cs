namespace AgentSupervisor.App.Bootstrap;

public static class BootstrapCommands
{
    public static bool IsPortable(string[] args) => args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
    public static void SelfInstall() { }
    public static void Uninstall() { }
    public static void ForwardHook() { }
}
