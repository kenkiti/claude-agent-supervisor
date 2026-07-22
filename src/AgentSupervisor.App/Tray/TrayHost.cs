using System.Windows.Forms;
namespace AgentSupervisor.App.Tray;

public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon icon;

    public TrayHost(NotifyIcon icon, Action onExit, Action onOpenDashboard)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => onOpenDashboard());
        menu.Items.Add("Exit", null, (_, _) => onExit());
        this.icon = icon;
        icon.Text = "AgentSupervisor";
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => onOpenDashboard();
    }

    public void Dispose() => icon.Dispose();
}
