using AgentSupervisor.Core;
using Microsoft.Extensions.Hosting;

namespace AgentSupervisor.Infrastructure;

public sealed class RuntimePoller : BackgroundService
{
    private readonly IReadOnlyList<IClaudeRuntime> _runtimes; private readonly SessionSnapshotStore _store;
    private readonly AlertEngine _alertEngine; private readonly INotificationOutbox _outbox;
    // App (where the SignalR hub lives) subscribes to this to push updates without Infrastructure
    // taking a dependency on ASP.NET Core/SignalR.
    public event Func<string, Task>? SnapshotSaved;
    public RuntimePoller(IEnumerable<IClaudeRuntime> runtimes, SessionSnapshotStore store, AlertEngine alertEngine, INotificationOutbox outbox)
    { _runtimes = runtimes.ToArray(); _store = store; _alertEngine = alertEngine; _outbox = outbox; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            foreach (var runtime in _runtimes)
            {
                try
                {
                    var agents = await runtime.ListAgentsAsync(true, stoppingToken);
                    var daemon = await runtime.GetDaemonStatusAsync(stoppingToken);
                    var auth = await runtime.GetAuthStatusAsync(stoppingToken);
                    var snapshot = new RuntimeSnapshot(runtime.RuntimeId, agents, daemon, auth, DateTimeOffset.UtcNow);
                    _store.Save(snapshot);
                    try
                    {
                        var candidate = _alertEngine.FromSnapshot(snapshot);
                        if (candidate is not null)
                            _outbox.Enqueue(candidate, _alertEngine.GetCooldownSeconds(candidate.NotificationType));
                    }
                    catch { }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        var snapshot = new RuntimeSnapshot(runtime.RuntimeId, Array.Empty<ClaudeAgent>(), new("", -1, "", ""), new("", -1, "", ""), DateTimeOffset.UtcNow, ex.Message);
                        _store.Save(snapshot);
                        try
                        {
                            var candidate = _alertEngine.FromSnapshot(snapshot);
                            if (candidate is not null)
                            {
                                _outbox.Enqueue(candidate, _alertEngine.GetCooldownSeconds(candidate.NotificationType));
                            }
                        }
                        catch { }
                    }
                    catch { }
                }
                if (SnapshotSaved is { } handler) { try { await handler(runtime.RuntimeId); } catch { } }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
