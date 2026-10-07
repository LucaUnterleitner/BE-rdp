// Session host mode: hosts one Remote Desktop session (Microsoft RDP ActiveX, mstscax.dll) per process.
// Started by the app as "BearingPoint.RemoteDesktop.exe --session-host --parent-pid <pid>". The app talks to it
// over stdin/stdout (one JSON object per line) and places its window inside a tab (SetParent). The host accepts a
// fixed set of commands only, never a password, and only attaches to windows that belong to its parent process.
// Port of native/BpRdpHost.cs from the Electron app.

using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace RdpManager.App.SessionHost;

[ComImport, Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(8)] void OnRequestGoFullScreen();
    [DispId(9)] void OnRequestLeaveFullScreen();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(22)] void OnLogonError(int lError);
    [DispId(23)] void OnFocusReleased(int iDirection);
    [DispId(33)] void OnAutoReconnected();
    [DispId(34)] void OnAutoReconnecting2(int disconnectReason, [MarshalAs(UnmanagedType.VariantBool)] bool networkAvailable, int attemptCount, int maxAttemptCount);
}

// Full vtable from IMsTscNonScriptable up to IMsRdpClientNonScriptable5 (from the mstscax type library).
[ComImport, Guid("4f6996d5-d7b1-412c-b0ff-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable5
{
    void set_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value0);
    void set_PortablePassword([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string get_PortablePassword();
    void set_PortableSalt([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string get_PortableSalt();
    void set_BinaryPassword([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string get_BinaryPassword();
    void set_BinarySalt([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string get_BinarySalt();
    void ResetPassword();
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys, [MarshalAs(UnmanagedType.LPArray)] short[] keyUp, [MarshalAs(UnmanagedType.LPArray)] int[] keyData);
    void set_UIParentWindowHandle(IntPtr hwnd);
    IntPtr get_UIParentWindowHandle();
    void set_ShowRedirectionWarningDialog([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ShowRedirectionWarningDialog();
    void set_PromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PromptForCredentials();
    void set_NegotiateSecurityLayer([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_NegotiateSecurityLayer();
    void set_EnableCredSspSupport([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_EnableCredSspSupport();
    void set_RedirectDynamicDrives([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_RedirectDynamicDrives();
    void set_RedirectDynamicDevices([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_RedirectDynamicDevices();
    [return: MarshalAs(UnmanagedType.IUnknown)] object get_DeviceCollection();
    [return: MarshalAs(UnmanagedType.IUnknown)] object get_DriveCollection();
    void set_WarnAboutSendingCredentials([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutSendingCredentials();
    void set_WarnAboutClipboardRedirection([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutClipboardRedirection();
    void set_ConnectionBarText([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string get_ConnectionBarText();
    void set_RedirectionWarningType(int v);
    int get_RedirectionWarningType();
    void set_MarkRdpSettingsSecure([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_MarkRdpSettingsSecure();
    void set_PublisherCertificateChain(ref object v);
    object get_PublisherCertificateChain();
    void set_WarnAboutPrinterRedirection([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutPrinterRedirection();
    void set_AllowCredentialSaving([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_AllowCredentialSaving();
    void set_PromptForCredsOnClient([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PromptForCredsOnClient();
    void set_LaunchedViaClientShellInterface([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_LaunchedViaClientShellInterface();
    void set_TrustedZoneSite([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_TrustedZoneSite();
    void set_UseMultimon([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_UseMultimon();
    uint get_RemoteMonitorCount();
    void GetRemoteMonitorsBoundingBox(ref int left, ref int top, ref int right, ref int bottom);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_RemoteMonitorLayoutMatchesLocal();
    void set_DisableConnectionBar([MarshalAs(UnmanagedType.VariantBool)] bool v);
    void set_DisableRemoteAppCapsCheck([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_DisableRemoteAppCapsCheck();
    void set_WarnAboutDirectXRedirection([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutDirectXRedirection();
    void set_AllowPromptingForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool v);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_AllowPromptingForCredentials();
}

[ComImport, Guid("302d8188-0052-4807-806a-362b628f9ac5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    void set_Property([MarshalAs(UnmanagedType.BStr)] string name, ref object value);
    object get_Property([MarshalAs(UnmanagedType.BStr)] string name);
}

internal sealed class RdpAxHost(string clsid) : AxHost(clsid)
{
    private AxHost.ConnectionPointCookie? _cookie;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public EventSink? Sink { get; set; }
    public object? Ocx => GetOcx();

    protected override void CreateSink()
    {
        try { _cookie = new AxHost.ConnectionPointCookie(GetOcx()!, Sink!, typeof(IMsTscAxEvents)); }
        catch (Exception ex) { SessionHostProgram.Send("error", "events: " + ex.Message); }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class EventSink : IMsTscAxEvents
{
    private readonly SessionForm _f;
    internal EventSink(SessionForm f) { _f = f; }
    public void OnConnecting() => SessionHostProgram.Send("connecting", null);
    public void OnConnected() => SessionHostProgram.Send("connected", null);
    public void OnLoginComplete() => _f.OnLoginComplete();
    public void OnDisconnected(int discReason) => _f.HandleDisconnected(discReason);
    public void OnRequestGoFullScreen() => SessionHostProgram.Send("requestFullscreen", true);
    public void OnRequestLeaveFullScreen() => SessionHostProgram.Send("requestFullscreen", false);
    public void OnFatalError(int errorCode)
    {
        SessionHostProgram.Send("fatalError", errorCode);
        SessionHostProgram.Fail($"The Remote Desktop component stopped with a fatal error (code {errorCode}). Reconnect to continue.");
    }
    public void OnWarning(int warningCode) => SessionHostProgram.Send("warning", warningCode);
    public void OnRequestContainerMinimize() => SessionHostProgram.Send("requestMinimize", null);
    public void OnLogonError(int lError) => SessionHostProgram.Send("logonError", lError);
    public void OnFocusReleased(int iDirection) => SessionHostProgram.Send("focusReleased", iDirection);
    public void OnAutoReconnected() => SessionHostProgram.Send("autoReconnected", null);
    public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount)
        => SessionHostProgram.Send("autoReconnecting", new Dictionary<string, object?> { ["reason"] = disconnectReason, ["attempt"] = attemptCount, ["max"] = maxAttemptCount });
}

internal sealed class SessionForm : Form
{
    // Newest first. MsRdpClient12 is registered on some builds but cannot be created there (CLASS_E_CLASSNOTAVAILABLE).
    private static readonly string[] Clsids =
    [
        "3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a", // MsRdpClient12NotSafeForScripting
        "1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8", // MsRdpClient11NotSafeForScripting
        "a0c63c30-f08d-4ab4-907c-34905d770c7d", // MsRdpClient10NotSafeForScripting
        "8b918b82-7985-4c24-89df-c33ad2bbfbcd", // MsRdpClient9NotSafeForScripting
    ];

    private readonly int _parentPid;
    private readonly RdpAxHost _ax = null!;
    private readonly dynamic _rdp = null!;
    private readonly System.Windows.Forms.Timer _resizeTimer;
    private bool _loggedIn;
    private bool _connectStarted;
    private int _pendingW, _pendingH, _pendingScale;

    public SessionForm(int parentPid)
    {
        _parentPid = parentPid;
        Text = "BearingPoint Remote Desktop session";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(31, 31, 31);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1024, 768);
        foreach (var clsid in Clsids)
        {
            RdpAxHost? a = null;
            try
            {
                a = new RdpAxHost(clsid) { Dock = DockStyle.Fill };
                a.Sink = new EventSink(this);
                ((ISupportInitialize)a).BeginInit();
                Controls.Add(a);
                ((ISupportInitialize)a).EndInit();
                a.CreateControl();
                if (a.Ocx is null) throw new InvalidOperationException("no control");
                _ax = a;
                _rdp = a.Ocx;
                SessionHostProgram.Send("ready", new Dictionary<string, object?> { ["clsid"] = clsid, ["version"] = (string)_rdp.Version });
                break;
            }
            catch (Exception ex)
            {
                if (a is not null) { Controls.Remove(a); a.Dispose(); }
                SessionHostProgram.Send("debug", "clsid " + clsid + " failed: " + ex.Message);
            }
        }
        if (_ax is null)
        {
            SessionHostProgram.Send("fatal", "The Remote Desktop ActiveX control is not available.");
            Environment.Exit(3);
        }
        // Resolution changes are sent to the server at most every 250 ms (the server rejects bursts).
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyResize(); };
    }

    // A tool window: no taskbar button and no hidden owner window.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // Fields are validated in the app; the host checks them again and accepts nothing else.
    public void Connect(JsonElement c)
    {
        if (_connectStarted) { SessionHostProgram.Send("error", "already connecting"); return; }
        var host = Str(c, "host");
        if (!InputCheck.Host(host)) { SessionHostProgram.Send("error", "invalid host"); return; }
        var user = Str(c, "username");
        if (user is not null && !InputCheck.User(user)) { SessionHostProgram.Send("error", "invalid username"); return; }
        var gw = Str(c, "gateway");
        if (!string.IsNullOrEmpty(gw) && !InputCheck.Host(gw)) { SessionHostProgram.Send("error", "invalid gateway"); return; }
        var port = Int(c, "port", 3389);
        if (port < 1 || port > 65535) { SessionHostProgram.Send("error", "invalid port"); return; }
        _connectStarted = true;

        _rdp.Server = host;
        if (!string.IsNullOrEmpty(user)) _rdp.UserName = user;
        _rdp.DesktopWidth = Math.Clamp(Int(c, "width", ClientSize.Width), 200, 8192);
        _rdp.DesktopHeight = Math.Clamp(Int(c, "height", ClientSize.Height), 200, 8192);
        _rdp.ColorDepth = 32;

        dynamic adv = _rdp.AdvancedSettings9;
        adv.RDPPort = port;
        adv.EnableCredSspSupport = true;
        adv.AuthenticationLevel = Int(c, "authLevel", 2) == 1 ? 1 : 2;
        adv.EnableAutoReconnect = true;
        adv.MaxReconnectAttempts = 20;
        adv.ContainerHandledFullScreen = 1;
        adv.SmartSizing = false;
        adv.GrabFocusOnConnect = true;
        adv.ConnectToAdministerServer = Bool(c, "adminSession");
        adv.RedirectClipboard = Bool(c, "clipboard");
        adv.RedirectDrives = Bool(c, "drives");
        adv.RedirectPrinters = Bool(c, "printers");
        adv.RedirectSmartCards = Bool(c, "smartcards");
        var audio = Str(c, "audio");
        adv.AudioRedirectionMode = audio == "remote" ? 1 : audio == "none" ? 2 : 0;
        adv.AudioCaptureRedirectionMode = Bool(c, "microphone");
        _rdp.SecuredSettings3.KeyboardHookMode = Bool(c, "keysToRemote") ? 1 : 2;

        var ns = (IMsRdpClientNonScriptable5)_ax.Ocx!;
        ns.set_ShowRedirectionWarningDialog(false);
        ns.set_AllowPromptingForCredentials(true);
        ns.set_AllowCredentialSaving(Bool(c, "allowCredentialSaving"));
        ns.set_NegotiateSecurityLayer(true);
        var top = Native.GetAncestor(Handle, Native.GA_ROOT);
        if (top != IntPtr.Zero && top != Handle) ns.set_UIParentWindowHandle(top);

        var ext = (IMsRdpExtendedSettings)_ax.Ocx!;
        SetExt(ext, "AllowAxToContainerEvents", true); // Ctrl+Alt+Left/Right gives the focus back to the app
        var scale = Int(c, "scale", 100);
        if (scale is >= 100 and <= 500) { SetExt(ext, "DesktopScaleFactor", (uint)scale); SetExt(ext, "DeviceScaleFactor", 100u); }
        var protection = Str(c, "credentialProtection");
        if (protection == "remoteGuard") { SetExt(ext, "RedirectedAuthentication", true); SetExt(ext, "DisableCredentialsDelegation", true); }
        if (protection == "restrictedAdmin") { SetExt(ext, "RestrictedLogon", true); SetExt(ext, "DisableCredentialsDelegation", true); }

        if (!string.IsNullOrEmpty(gw))
        {
            dynamic ts = _rdp.TransportSettings4;
            ts.GatewayHostname = gw;
            ts.GatewayUsageMethod = Str(c, "gatewayMode") == "detect" ? 2 : 1;
            ts.GatewayProfileUsageMethod = 1;
            ts.GatewayCredsSource = 4;
            ts.GatewayCredSharing = 1;
        }
        _rdp.Connect(); // No password: CredSSP uses a saved TERMSRV/<host> credential or Windows asks.
    }

    private static void SetExt(IMsRdpExtendedSettings e, string name, object v)
    {
        try { e.set_Property(name, ref v); }
        catch (Exception ex) { SessionHostProgram.Send("debug", "setting " + name + ": " + ex.Message); }
    }

    public void OnLoginComplete()
    {
        _loggedIn = true;
        SessionHostProgram.Send("loginComplete", null);
        if (_pendingW > 0) _resizeTimer.Start();
    }

    public void HandleDisconnected(int reason)
    {
        var text = "";
        var ext = 0;
        try
        {
            ext = (int)_rdp.ExtendedDisconnectReason;
            text = (string)_rdp.GetErrorDescription((uint)reason, (uint)ext);
        }
        catch (Exception) { /* optional detail */ }
        SessionHostProgram.Send("disconnected", new Dictionary<string, object?> { ["reason"] = reason, ["extended"] = ext, ["text"] = text });
        BeginInvoke(() => Application.Exit());
    }

    // Places the session window inside a window of the parent process (client coordinates, physical pixels),
    // or makes it a hidden top-level window again (parent 0).
    public void Attach(long parent, int x, int y, int w, int h, bool visible)
    {
        var hwnd = Handle;
        var p = new IntPtr(parent);
        if (parent != 0)
        {
            Native.GetWindowThreadProcessId(p, out var owner);
            if (owner != (uint)_parentPid) { SessionHostProgram.Send("error", "parent window does not belong to the app"); return; }
        }
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        if (parent != 0)
        {
            style = (style & ~Native.WS_POPUP) | Native.WS_CHILD | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN;
            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style));
            if (Native.GetParent(hwnd) != p) Native.SetParent(hwnd, p);
        }
        else
        {
            Native.SetParent(hwnd, IntPtr.Zero);
            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr((style & ~Native.WS_CHILD) | Native.WS_POPUP));
            x = -32000; y = -32000; visible = false;
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOP, x, y, Math.Max(1, w), Math.Max(1, h),
            Native.SWP_FRAMECHANGED | (visible ? Native.SWP_SHOWWINDOW : Native.SWP_HIDEWINDOW));
        if (parent != 0 && _connectStarted)
        {
            try { ((IMsRdpClientNonScriptable5)_ax.Ocx!).set_UIParentWindowHandle(Native.GetAncestor(hwnd, Native.GA_ROOT)); }
            catch (Exception) { /* optional */ }
        }
    }

    public void FocusSession() => Native.SetFocus(_ax.Handle);

    public void ResizeSession(int w, int h, int scale)
    {
        _pendingW = w; _pendingH = h; _pendingScale = scale;
        if (!_loggedIn) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ApplyResize()
    {
        try
        {
            if ((short)_rdp.Connected != 1) return;
            var w = Math.Clamp(_pendingW, 200, 8192) & ~1; // even width, as the protocol expects
            var h = Math.Clamp(_pendingH, 200, 8192);
            var s = _pendingScale is >= 100 and <= 500 ? _pendingScale : 100;
            _rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, (uint)s, 100u);
        }
        catch (Exception ex) { SessionHostProgram.Send("debug", "resize: " + ex.Message); }
    }

    public void DisconnectSession()
    {
        try
        {
            if ((short)_rdp.Connected != 0) { _rdp.Disconnect(); return; }
        }
        catch (Exception) { /* fall through */ }
        Application.Exit();
    }

    private static string? Str(JsonElement d, string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement d, string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement d, string k, int fallback) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;
}

internal static class InputCheck
{
    public static bool Host(string? h)
    {
        if (string.IsNullOrEmpty(h) || h.Length > 253 || h[0] == '-' || h[0] == '.') return false;
        foreach (var ch in h)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':')) return false;
        return true;
    }

    public static bool User(string u)
    {
        if (u.Length > 256) return false;
        foreach (var ch in u) if (ch < 32 || ch == '"' || ch == 127) return false;
        return true;
    }
}

internal static partial class Native
{
    public const int GWL_STYLE = -16;
    public const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CLIPSIBLINGS = 0x04000000L, WS_CLIPCHILDREN = 0x02000000L;
    public const uint SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    public const uint GA_ROOT = 2;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [LibraryImport("user32.dll", SetLastError = true)] public static partial IntPtr SetParent(IntPtr child, IntPtr parent);
    [LibraryImport("user32.dll")] public static partial IntPtr GetParent(IntPtr h);
    [LibraryImport("user32.dll")] public static partial IntPtr GetAncestor(IntPtr h, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial IntPtr GetWindowLongPtr(IntPtr h, int i);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial IntPtr SetFocus(IntPtr h);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}

/// <summary>Entry point of the session host mode.</summary>
internal static class SessionHostProgram
{
    private static readonly object OutLock = new();
    private static SessionForm? _form;
    private static Stream? _out;

    public static void Send(string type, object? payload)
    {
        var buffer = new MemoryStream(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WritePropertyName("data");
            Write(w, payload);
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        lock (OutLock)
        {
            try { _out?.Write(buffer.GetBuffer(), 0, (int)buffer.Length); _out?.Flush(); }
            catch (IOException) { /* app gone */ }
        }
    }

    private static void Write(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case Dictionary<string, object?> d:
                w.WriteStartObject();
                foreach (var (k, x) in d) { w.WritePropertyName(k); Write(w, x); }
                w.WriteEndObject();
                break;
            default: w.WriteStringValue(v.ToString()); break;
        }
    }

    public static int Run(string[] args)
    {
        var parentPid = 0;
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--parent-pid") _ = int.TryParse(args[i + 1], out parentPid);
        if (parentPid <= 0) return 2;
        _out = Console.OpenStandardOutput();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        _form = new SessionForm(parentPid);
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "session-host-stdin" };
        _form.Load += (_, _) => { Send("window", (long)_form.Handle); reader.Start(); };
        Application.Run(_form);
        return 0;
    }

    private static void ReadLoop()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length > 16384) { Send("error", "line too long"); continue; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { Send("error", "bad json"); continue; }
            try { _form!.BeginInvoke(() => { using (doc) Dispatch(doc.RootElement); }); }
            catch (InvalidOperationException) { return; }
        }
        // The app is gone: end the session and exit.
        try { _form!.BeginInvoke(() => { _form.DisconnectSession(); Application.Exit(); }); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Reports an unrecoverable problem as a disconnect with an explanation, then exits.</summary>
    public static void Fail(string text)
    {
        Send("disconnected", new Dictionary<string, object?> { ["reason"] = 0, ["extended"] = 0, ["text"] = text });
        try { _form?.BeginInvoke(() => Application.Exit()); } catch (InvalidOperationException) { Environment.Exit(4); }
    }

    private static void Dispatch(JsonElement m)
    {
        try
        {
            var cmd = m.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var a = m.TryGetProperty("args", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
            int I(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
            switch (cmd)
            {
                case "connect":
                    if (a.ValueKind != JsonValueKind.Object) break;
                    try { _form!.Connect(a); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // The control rejected a setting or the connect call: end the session so the tab does not wait forever.
                        Fail("The Remote Desktop component could not start the connection: " + ex.Message);
                    }
                    break;
                case "attach":
                    var parent = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("parent", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt64() : 0;
                    var visible = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("visible", out var vv) && vv.ValueKind == JsonValueKind.True;
                    _form!.Attach(parent, I("x"), I("y"), I("w"), I("h"), visible);
                    break;
                case "focus": _form!.FocusSession(); break;
                case "resize": _form!.ResizeSession(I("w"), I("h"), I("scale")); break;
                case "disconnect": _form!.DisconnectSession(); break;
                default: Send("error", "unknown command"); break; // deliberately no generic property setter
            }
        }
        catch (Exception ex) { Send("error", ex.GetType().Name + ": " + ex.Message); }
    }
}
