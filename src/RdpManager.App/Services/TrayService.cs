using System.Windows.Forms;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Services;

/// <summary>Notification area icon with active sessions and recent systems (created after the first frame).</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppController _controller;
    private readonly UiFlows _flows;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly NotifyIcon _icon;

    public TrayService(AppController controller, UiFlows flows, Action show, Action quit)
    {
        _controller = controller;
        _flows = flows;
        _show = show;
        _quit = quit;
        System.Drawing.Icon? ico = null;
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))?.Stream;
            if (stream is not null) ico = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) { DiagnosticLog.Warn("Tray icon could not be loaded", e); }
        _icon = new NotifyIcon { Icon = ico ?? System.Drawing.SystemIcons.Application, Text = "BearingPoint Remote Desktop", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _show(); };
        UpdateMenu();
    }

    public void UpdateMenu()
    {
        var live = _controller.Sessions.List().Where(s => s.IsLive).ToList();
        var recent = _controller.RecentConnections(5);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open BearingPoint Remote Desktop", null, (_, _) => _show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(live.Count > 0 ? $"{live.Count} active {(live.Count == 1 ? "session" : "sessions")}" : "No active sessions") { Enabled = false });
        foreach (var s in live)
        {
            var id = s.Id;
            menu.Items.Add($"Show {s.Name}", null, (_, _) => { try { _controller.FocusSession(id); } catch (InvalidOperationException) { _show(); } });
        }
        menu.Items.Add(new ToolStripSeparator());
        var connect = new ToolStripMenuItem("Connect to") { Enabled = recent.Count > 0 };
        foreach (var c in recent)
        {
            var id = c.Id;
            connect.DropDownItems.Add(c.Name, null, (_, _) => { _show(); _flows.HandleConnectRequest(id); });
        }
        menu.Items.Add(connect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _quit());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
        var tip = live.Count > 0 ? $"BearingPoint Remote Desktop: {live.Count} active" : "BearingPoint Remote Desktop";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Balloon(string text) => _icon.ShowBalloonTip(5000, "BearingPoint Remote Desktop", text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
