using System.Windows.Forms;
namespace AgentSupervisor.App.Tray;

public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon icon;

    public TrayHost(NotifyIcon icon, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => onExit());
        this.icon = icon;
        icon.Text = "AgentSupervisor";
        icon.ContextMenuStrip = menu;
    }

    public void Dispose() => icon.Dispose();
}
