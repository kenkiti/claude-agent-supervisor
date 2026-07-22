namespace AgentSupervisor.Infrastructure;

public sealed class AppShutdownCoordinator
{
    private readonly object _lock = new();
    private bool _exited;
    public Action? ExitAction { get; set; }

    public void RequestExit()
    {
        lock (_lock)
        {
            if (_exited) return;
            _exited = true;
        }
        ExitAction?.Invoke();
    }
}
