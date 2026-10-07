using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RdpManager.App.Views;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Sessions;

namespace RdpManager.App.Sessions;

/// <summary>
/// Session tabs (port of tabs.js). Every app window is a tab host: the main window (tabs next to "Home") and
/// windows made from tabs dragged out of it. A session host window (another process) is a child of the
/// <see cref="Controls.SessionSlot"/> of the window that shows its tab.
///
/// Moving a tab: drag it out of the tab strip (a new window follows the cursor), drop it on another window's tab
/// strip to dock it there, or release it at the top edge of a screen for full screen. Closing a detached window
/// moves its tabs back to the main window. Before a slot can go away, its sessions are parked in a hidden window
/// of this process, because Windows destroys child windows together with their parent.
/// </summary>
public sealed partial class SessionTabManager(SessionManager sessions)
{
    private const int TopEdgePx = 4;
    private readonly List<TabHost> _hosts = [];
    private readonly Dictionary<string, SessionHostClient> _clients = [];
    private readonly HashSet<string> _shown = [];
    private HwndSource? _parking;
    private DragState? _drag;

    private sealed class DragState
    {
        public required string SessionId { get; init; }
        public required TabHost Host { get; init; }
        public required TabHost From { get; init; }
        public Point Offset { get; init; }
        public TabHost? Hint { get; set; }
        public DispatcherTimer? Timer { get; set; }
    }

    public TabHost? Main { get; private set; }
    public IReadOnlyList<TabHost> Hosts => _hosts;
    private static Dispatcher Ui => Application.Current.Dispatcher;

    /// <summary>Raised on the UI thread when tabs were added, removed or moved.</summary>
    public event Action? Changed;

    public void RegisterMain(TabHost host)
    {
        Main = host;
        Register(host);
    }

    private void Register(TabHost host)
    {
        _hosts.Add(host);
        host.Slot.SlotChanged += () => Sync(host);
        host.Window.LocationChanged += (_, _) => { if (!host.IsMain && _drag is null && !host.Closing) DockIfOverStrip(host); };
        host.Window.StateChanged += (_, _) => Sync(host);
    }

    public bool Has(string sessionId) => _clients.ContainsKey(sessionId);

    public TabHost? HostOf(string sessionId) => _hosts.FirstOrDefault(h => h.Tabs.Any(t => t.Id == sessionId));

    private SessionTab? TabOf(string sessionId) => HostOf(sessionId)?.Tabs.FirstOrDefault(t => t.Id == sessionId);

    // ── Start ────────────────────────────────────────────
    /// <summary>
    /// Starts a session host, opens a tab for it and connects. The tab is shown before the connection starts,
    /// so the size is known and Windows sign-in prompts belong to the app window.
    /// </summary>
    public async Task<SessionInfo> StartEmbeddedAsync(Connection conn, bool keysToRemote, bool allowCredentialSaving)
    {
        var client = await Task.Run(() => new SessionHostClient()).ConfigureAwait(false);
        try
        {
            await client.WhenReadyAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch
        {
            client.Kill();
            client.Dispose();
            throw;
        }
        try
        {
        return await Ui.InvokeAsync(async () =>
        {
            // The host may have died between "ready" and here; then use a separate window instead.
            if (client.HasExited) throw new InvalidOperationException("The Remote Desktop component stopped unexpectedly.");
            var info = new SessionInfo
            {
                Id = Guid.NewGuid().ToString(),
                Protocol = Protocols.Rdp,
                ConnectionId = conn.Id,
                Name = conn.Name,
                Host = conn.Host,
                Port = conn.Port == 0 ? 3389 : conn.Port,
                Username = conn.Username,
                Gateway = conn.Gateway is { Mode: not "none" } g ? g.Host : "",
                DisplayMode = "Tab in the app",
                LaunchMode = "embedded",
                Pid = client.Pid,
                Verified = true,
            };
            _clients[info.Id] = client;
            WireEvents(info, client);
            sessions.RegisterEmbedded(info, client.Disconnect, () => Ui.Invoke(() => Focus(info.Id)));
            var host = Main!;
            host.Tabs.Add(new SessionTab(info.Id, conn.Id, conn.Name, conn.Host));
            Activate(host, info.Id);
            host.Window.Show();
            host.Window.Activate();
            Changed?.Invoke();
            await Task.Delay(150); // the window lays out the new tab
            Sync(host);
            var (w, h) = host.Slot.PixelSize;
            var dpi = VisualTreeHelper.GetDpi(host.Slot);
            if (w < 100 || h < 100) { w = (int)(1280 * dpi.DpiScaleX); h = (int)(800 * dpi.DpiScaleY); }
            client.Send("connect", new
            {
                host = conn.Host,
                port = conn.Port == 0 ? 3389 : conn.Port,
                username = string.IsNullOrEmpty(conn.Username) ? null : conn.Username,
                width = w,
                height = h,
                scale = (int)Math.Round(dpi.DpiScaleX * 100),
                authLevel = conn.Security!.AuthLevel,
                adminSession = conn.Security.AdminSession,
                credentialProtection = conn.Security.CredentialProtection,
                clipboard = conn.Redirect!.Clipboard,
                drives = conn.Redirect.Drives == "all",
                printers = conn.Redirect.Printers,
                smartcards = conn.Redirect.Smartcards,
                microphone = conn.Redirect.Microphone,
                audio = conn.Redirect.Audio,
                gateway = conn.Gateway is { Mode: not "none" } gw ? gw.Host : null,
                gatewayMode = conn.Gateway?.Mode ?? "none",
                allowCredentialSaving,
                keysToRemote,
            });
            return info;
        }).Task.Unwrap().ConfigureAwait(false);
        }
        catch
        {
            // Never leave a host process behind; its exit handler removes a tab that was already added.
            client.Kill();
            throw;
        }
    }

    /// <summary>Session state from the ActiveX events (port of SessionManager.startEmbedded).</summary>
    private void WireEvents(SessionInfo info, SessionHostClient client)
    {
        var finished = false;
        client.Message += (type, data) => Ui.BeginInvoke(() =>
        {
            switch (type)
            {
                case "connected":
                    _shown.Add(info.Id);
                    if (TabOf(info.Id) is { } shownTab) shownTab.Shown = true;
                    if (HostOf(info.Id) is { } h) Sync(h);
                    break;
                case "loginComplete":
                    sessions.Update(info.Id, s =>
                    {
                        if (!s.IsLive) return;
                        s.State = SessionState.Active;
                        s.ConnectedAt ??= DateTimeOffset.UtcNow;
                        s.Result = null;
                    });
                    break;
                case "autoReconnecting":
                    var reason = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var r) && r.TryGetInt32(out var rv) ? rv : (int?)null;
                    sessions.Update(info.Id, s => { if (s.State == SessionState.Active) { s.State = SessionState.Reconnecting; s.Result = DisconnectReasons.Explain(reason); } });
                    break;
                case "autoReconnected":
                    sessions.Update(info.Id, s => { if (s.State == SessionState.Reconnecting) { s.State = SessionState.Active; s.Result = null; } });
                    break;
                case "logonError":
                    // For example a wrong password: the control lets the user try again, so only show a hint.
                    var code = data.ValueKind == JsonValueKind.Number ? data.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                    sessions.Update(info.Id, s => { if (s.State == SessionState.Connecting) s.Result = new SessionResult("error", "Sign-in did not work", "Check your username and password.", code, Hint: true); });
                    break;
                case "disconnected":
                    finished = true;
                    OnDisconnected(info.Id, data);
                    break;
                case "requestFullscreen":
                    if (HostOf(info.Id) is { } fh) SetFullscreen(fh, data.ValueKind == JsonValueKind.True);
                    break;
                case "focusReleased":
                    HostOf(info.Id)?.Window.Activate();
                    break;
            }
        });
        client.Exited += () => Ui.BeginInvoke(() =>
        {
            sessions.MarkStopped(info.Id);
            var current = sessions.Get(info.Id);
            if (!finished && current is { IsLive: true })
            {
                sessions.Update(info.Id, s =>
                {
                    s.State = SessionState.Failed;
                    s.EndedAt = DateTimeOffset.UtcNow;
                    s.Result = new SessionResult("error", "The session stopped unexpectedly", "The Remote Desktop component in the app closed. Reconnect to continue; your remote session is still running on the server.");
                });
            }
            Remove(info.Id);
            client.Dispose();
        });
    }

    private void OnDisconnected(string id, JsonElement d)
    {
        var reason = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("reason", out var r) && r.TryGetInt32(out var v) ? v : (int?)null;
        var text = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var userDisconnect = sessions.WasUserDisconnect(id);
        sessions.Update(id, s =>
        {
            var wasConnected = s.State is SessionState.Active or SessionState.Reconnecting;
            var result = DisconnectReasons.Explain(reason);
            if (result is { IsError: true } && !string.IsNullOrEmpty(text) && !DisconnectReasons.IsKnown(reason)) result = result with { Message = text };
            var state = result is { IsError: true } ? SessionState.Failed : SessionState.Ended;
            if (userDisconnect)
            {
                state = SessionState.Ended;
                result = wasConnected
                    ? new SessionResult("normal", "Disconnected", "You closed the tab. Your remote session keeps running on the server until you sign out inside it.")
                    : new SessionResult("normal", "Connection cancelled", "You cancelled the connection before the session was established.");
            }
            s.State = state;
            s.EndedAt = DateTimeOffset.UtcNow;
            s.Result = result ?? new SessionResult("normal", "Session closed", "The remote session ended.");
        });
    }

    /// <summary>Called on every session update (UI thread): keeps tab states in sync.</summary>
    public void OnSessionUpdate(SessionInfo s)
    {
        if (TabOf(s.Id) is { } tab) tab.State = s.State;
    }

    private void Remove(string sessionId)
    {
        var host = HostOf(sessionId);
        _clients.Remove(sessionId);
        _shown.Remove(sessionId);
        if (host is null) return;
        var tab = host.Tabs.First(t => t.Id == sessionId);
        var i = host.Tabs.IndexOf(tab);
        host.Tabs.Remove(tab);
        if (host.ActiveId == sessionId) host.ActiveId = host.Tabs.Count > 0 ? host.Tabs[Math.Min(i, host.Tabs.Count - 1)].Id : null;
        if (host.Tabs.Count == 0 && host.Fullscreen) SetFullscreen(host, false);
        host.NotifyTabsChanged();
        Sync(host);
        if (!host.IsMain && host.Tabs.Count == 0) CloseHost(host);
        UpdateElsewhere();
        Changed?.Invoke();
    }

    // ── Activation and focus ─────────────────────────────
    /// <summary>Shows a tab (null in the main window shows the app itself).</summary>
    public void Activate(TabHost host, string? sessionId)
    {
        if (sessionId is null && !host.IsMain) return;
        host.ActiveId = sessionId;
        Sync(host);
        if (sessionId is not null) FocusSoon(sessionId);
        UpdatePinbar(host);
    }

    /// <summary>Brings a session to the front: its window, its tab and the keyboard focus.</summary>
    public void Focus(string sessionId)
    {
        var host = HostOf(sessionId) ?? throw new InvalidOperationException("This session is no longer open.");
        if (host.Window.WindowState == WindowState.Minimized) host.Window.WindowState = WindowState.Normal;
        host.Window.Show();
        host.Window.Activate();
        Activate(host, sessionId);
    }

    private void FocusSoon(string sessionId)
    {
        var client = _clients.GetValueOrDefault(sessionId); // read on the UI thread, used after the delay
        if (client is not null) _ = Task.Delay(80).ContinueWith(_ => client.Send("focus"), TaskScheduler.Default);
    }

    // ── Placement ────────────────────────────────────────
    /// <summary>Attaches every session of a host to its slot; only the active one is visible.</summary>
    public void Sync(TabHost host)
    {
        if (host.Closing) return;
        var slot = host.Slot.SlotHandle;
        var (w, h) = host.Slot.PixelSize;
        var dpi = host.Slot.IsLoaded ? VisualTreeHelper.GetDpi(host.Slot).DpiScaleX : 1.0;
        var slotVisible = host.Window.WindowState != WindowState.Minimized;
        foreach (var tab in host.Tabs)
        {
            if (!_clients.TryGetValue(tab.Id, out var client)) continue;
            var visible = tab.Id == host.ActiveId && slotVisible && _shown.Contains(tab.Id) && _drag?.SessionId != tab.Id && w > 0 && h > 0 && slot != IntPtr.Zero;
            if (slot == IntPtr.Zero) continue;
            var key = $"{slot}:{w}:{h}:{visible}";
            if (client.LastAttach != key)
            {
                client.LastAttach = key;
                client.Send("attach", new { parent = slot.ToInt64(), x = 0, y = 0, w, h, visible });
            }
            var sizeKey = $"{w}x{h}@{dpi}";
            if (visible && client.LastSize != sizeKey)
            {
                client.LastSize = sizeKey;
                client.Send("resize", new { w, h, scale = (int)Math.Round(dpi * 100) });
            }
        }
    }

    [LibraryImport("user32.dll")] private static partial IntPtr SetParent(IntPtr child, IntPtr parent);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>Moves a session window synchronously into a hidden window of this process (before its slot goes away).</summary>
    private void Park(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var c) || c.WindowHandle == IntPtr.Zero) return;
        _parking ??= new HwndSource(new HwndSourceParameters("BearingPoint Remote Desktop parking") { WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000, Width = 1, Height = 1 });
        ShowWindow(c.WindowHandle, 0);
        SetParent(c.WindowHandle, _parking.Handle);
        c.LastAttach = null;
    }

    public void MoveSession(string sessionId, TabHost to, bool activate = true)
    {
        var from = HostOf(sessionId);
        if (from == to || from is null) return;
        Park(sessionId);
        var tab = from.Tabs.First(t => t.Id == sessionId);
        from.Tabs.Remove(tab);
        if (from.ActiveId == sessionId) from.ActiveId = from.Tabs.FirstOrDefault()?.Id;
        from.NotifyTabsChanged();
        to.Tabs.Add(tab);
        if (activate) to.ActiveId = sessionId;
        to.NotifyTabsChanged();
        Sync(from);
        Sync(to);
        UpdateElsewhere();
        UpdateTitle(to);
        Changed?.Invoke();
    }

    /// <summary>Moves all tabs of a window into another window and closes the emptied one.</summary>
    public void MergeInto(TabHost from, TabHost to)
    {
        if (from == to) return;
        foreach (var t in from.Tabs.ToList()) MoveSession(t.Id, to);
        CloseHost(from);
        to.Window.Show();
        to.Window.Activate();
    }

    private void CloseHost(TabHost host)
    {
        if (host.IsMain || host.Tabs.Count > 0) return;
        host.Closing = true;
        ClosePinbar(host);
        _hosts.Remove(host);
        host.Window.Close();
        UpdateElsewhere();
    }

    /// <summary>Detached window closing: its sessions go back to the main window.</summary>
    public void OnHostClosing(TabHost host)
    {
        if (host.Closing || host.IsMain) return;
        foreach (var t in host.Tabs.ToList()) MoveSession(t.Id, Main!);
        host.Closing = true;
        ClosePinbar(host);
        _hosts.Remove(host);
        UpdateElsewhere();
    }

    /// <summary>Main window closing for good: park all sessions so their windows survive until they disconnect.</summary>
    public void ParkAll()
    {
        foreach (var id in _clients.Keys.ToList()) Park(id);
    }

    public void DetachToNewWindow(string sessionId)
    {
        var from = HostOf(sessionId);
        if (from is null) return;
        var host = NewHost(new Point(from.Window.Left + 140, from.Window.Top + 140));
        MoveSession(sessionId, host);
        host.Window.Show();
        CloseHost(from);
    }

    private TabHost NewHost(Point topLeft)
    {
        var main = Main!;
        var window = new SessionWindow(this)
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Width = Math.Max(800, main.Window.ActualWidth * 0.8),
            Height = Math.Max(560, main.Window.ActualHeight * 0.8),
        };
        var host = window.Host;
        Register(host);
        return host;
    }

    private void UpdateElsewhere()
    {
        if (Main is not null) Main.Elsewhere = _hosts.Any(h => !h.IsMain && h.Tabs.Count > 0);
    }

    private static void UpdateTitle(TabHost host)
    {
        if (host.IsMain) return;
        host.Window.Title = $"{host.ActiveTab?.Name ?? "Sessions"} – BearingPoint Remote Desktop";
    }

    // ── Context menu ─────────────────────────────────────
    public void ShowMenu(TabHost host, string sessionId, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var fs = new MenuItem { Header = host.Fullscreen ? "Leave full screen" : "Full screen", InputGestureText = "Ctrl+Alt+Break" };
        fs.Click += (_, _) => ToggleFullscreen(sessionId);
        menu.Items.Add(fs);
        var detach = new MenuItem { Header = "Move to new window", IsEnabled = host.Tabs.Count > 1 || host.IsMain };
        detach.Click += (_, _) => DetachToNewWindow(sessionId);
        menu.Items.Add(detach);
        foreach (var other in _hosts.Where(h => h != host).ToList())
        {
            var item = new MenuItem { Header = other.IsMain ? "Move to main window" : $"Move to window with {other.ActiveTab?.Name ?? "sessions"}" };
            item.Click += (_, _) => { MoveSession(sessionId, other); CloseHost(host); other.Window.Activate(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var disc = new MenuItem { Header = "Disconnect" };
        disc.Click += (_, _) => sessions.Disconnect(sessionId);
        menu.Items.Add(disc);
        menu.IsOpen = true;
    }

    // ── Full screen ──────────────────────────────────────
    public void ToggleFullscreen(string sessionId)
    {
        var host = HostOf(sessionId);
        if (host is null) return;
        if (host.ActiveId != sessionId) Activate(host, sessionId);
        SetFullscreen(host, !host.Fullscreen);
    }

    public void SetFullscreen(TabHost host, bool on)
    {
        var w = host.Window;
        if (on == host.Fullscreen) return;
        if (on)
        {
            host.SavedWindow = (w.WindowState, w.WindowStyle, w.ResizeMode, w.Topmost);
            w.WindowState = WindowState.Normal; // borderless + maximized covers the whole monitor, including the taskbar
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.NoResize;
            w.WindowState = WindowState.Maximized;
        }
        else if (host.SavedWindow is { } saved)
        {
            w.WindowState = WindowState.Normal;
            w.WindowStyle = saved.Style;
            w.ResizeMode = saved.Resize;
            w.WindowState = saved.State;
        }
        host.Fullscreen = on;
        UpdatePinbar(host);
        Ui.BeginInvoke(DispatcherPriority.Background, () => Sync(host));
    }

    private void UpdatePinbar(TabHost host)
    {
        if (!host.Fullscreen || host.ActiveTab is null) { ClosePinbar(host); return; }
        if (host.Pinbar is null)
        {
            host.Pinbar = new PinBarWindow(this, host);
            host.Pinbar.Show();
        }
        host.Pinbar.Refresh();
    }

    private static void ClosePinbar(TabHost host)
    {
        host.Pinbar?.Close();
        host.Pinbar = null;
    }

    public void PinbarAction(TabHost host, string action)
    {
        switch (action)
        {
            case "leave": SetFullscreen(host, false); break;
            case "dock" when !host.IsMain: SetFullscreen(host, false); MergeInto(host, Main!); break;
            case "disconnect" when host.ActiveId is not null: sessions.Disconnect(host.ActiveId); break;
        }
    }

    // ── Dragging tabs out of a window ────────────────────
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out NativePoint p);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vKey);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    private static Point CursorPx() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : default;

    private static bool PrimaryButtonDown()
    {
        var swapped = GetSystemMetrics(23) != 0; // SM_SWAPBUTTON
        return (GetAsyncKeyState(swapped ? 0x02 : 0x01) & 0x8000) != 0;
    }

    /// <summary>The host whose tab strip is under the cursor (physical screen pixels).</summary>
    private TabHost? StripAt(Point px, TabHost? except)
    {
        foreach (var host in _hosts)
        {
            if (host == except || host.Closing || !host.Window.IsVisible || host.Window.WindowState == WindowState.Minimized) continue;
            FrameworkElement target = host.IsMain ? (FrameworkElement)host.Window.Content : host.Strip;
            if (!target.IsVisible) continue;
            var tl = target.PointToScreen(new Point(0, 0));
            // In the main window the whole top area (header and tab bar) accepts a dropped tab.
            var height = host.IsMain ? host.Strip.TranslatePoint(new Point(0, host.Strip.ActualHeight), target).Y + 52 : host.Strip.ActualHeight;
            var br = target.PointToScreen(new Point(target.ActualWidth, Math.Max(height, 40)));
            if (px.X >= tl.X && px.X <= br.X && px.Y >= tl.Y - 12 && px.Y <= br.Y + 12) return host;
        }
        return null;
    }

    private void DockIfOverStrip(TabHost host)
    {
        if (!PrimaryButtonDown()) return;
        var target = StripAt(CursorPx(), host);
        if (target is not null) Ui.BeginInvoke(DispatcherPriority.Background, () => { if (!PrimaryButtonDown()) return; MergeInto(host, target); });
    }

    public void DragStart(TabHost from, string sessionId)
    {
        if (_drag is not null || from.Fullscreen || HostOf(sessionId) != from) return;
        var cursor = CursorPx();
        TabHost host;
        if (!from.IsMain && from.Tabs.Count == 1)
        {
            host = from; // the window holds only this tab: move the window itself
        }
        else
        {
            var dips = from.Window.PointFromScreen(cursor);
            var screenDip = new Point(from.Window.Left + dips.X, from.Window.Top + dips.Y);
            host = NewHost(new Point(screenDip.X - 90, screenDip.Y - 52));
            MoveSession(sessionId, host);
            host.Window.ShowActivated = false;
            host.Window.Show();
        }
        var origin = host.Window.PointToScreen(new Point(0, 0));
        _drag = new DragState { SessionId = sessionId, Host = host, From = from, Offset = new Point(cursor.X - origin.X, cursor.Y - origin.Y) };
        Sync(host);
        _drag.Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => DragTick(), Ui);
        _drag.Timer.Start();
    }

    private void DragTick()
    {
        var d = _drag;
        if (d is null) return;
        if (!PrimaryButtonDown() || !d.Host.Window.IsLoaded) { DragEnd(); return; }
        var p = CursorPx();
        var scale = VisualTreeHelper.GetDpi(d.Host.Window);
        d.Host.Window.Left = (p.X - d.Offset.X) / scale.DpiScaleX;
        d.Host.Window.Top = (p.Y - d.Offset.Y) / scale.DpiScaleY;
        var target = StripAt(p, d.Host);
        if (target != d.Hint)
        {
            if (d.Hint is not null) d.Hint.DropHint = false;
            if (target is not null) target.DropHint = true;
            d.Hint = target;
        }
    }

    private void DragEnd()
    {
        var d = _drag;
        if (d is null) return;
        d.Timer?.Stop();
        _drag = null;
        if (d.Hint is not null) d.Hint.DropHint = false;
        var p = CursorPx();
        var target = StripAt(p, d.Host);
        if (target is not null)
        {
            MoveSession(d.SessionId, target);
            CloseHost(d.Host);
            target.Window.Activate();
            FocusSoon(d.SessionId);
        }
        else
        {
            d.Host.Window.Activate();
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)p.X, (int)p.Y));
            if (p.Y <= screen.Bounds.Top + TopEdgePx) SetFullscreen(d.Host, true);
            Sync(d.Host);
            FocusSoon(d.SessionId);
        }
        if (d.From != d.Host) CloseHost(d.From);
    }
}
