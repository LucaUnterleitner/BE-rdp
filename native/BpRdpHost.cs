// BpRdpHost: hosts one Remote Desktop session (Microsoft RDP ActiveX, mstscax.dll) for BearingPoint Remote Desktop.
// One process per session. The Electron main process starts it, talks to it over stdin/stdout (one JSON object
// per line) and places its window inside a tab of an app window (SetParent). The helper accepts a fixed set of
// commands only, never a password, and only attaches to windows that belong to its parent process.
//
// Build (in-box compiler, no SDK needed): see tools/build-helper.js.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BpRdpHost
{
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

    // Full vtable from IMsTscNonScriptable up to IMsRdpClientNonScriptable5 (generated from the mstscax type library).
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

    public class RdpAxHost : AxHost
    {
        public RdpAxHost(string clsid) : base(clsid) { }
        public EventSink Sink;
        ConnectionPointCookie cookie;
        protected override void CreateSink()
        {
            try { cookie = new ConnectionPointCookie(GetOcx(), Sink, typeof(IMsTscAxEvents)); }
            catch (Exception ex) { Program.Send("error", "events: " + ex.Message); }
        }
        protected override void DetachSink() { if (cookie != null) { cookie.Disconnect(); cookie = null; } }
        public object Ocx { get { return GetOcx(); } }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class EventSink : IMsTscAxEvents
    {
        readonly SessionForm f;
        public EventSink(SessionForm f) { this.f = f; }
        public void OnConnecting() { Program.Send("connecting", null); }
        public void OnConnected() { Program.Send("connected", null); }
        public void OnLoginComplete() { f.OnLoginComplete(); }
        public void OnDisconnected(int reason) { f.HandleDisconnected(reason); }
        public void OnRequestGoFullScreen() { Program.Send("requestFullscreen", true); }
        public void OnRequestLeaveFullScreen() { Program.Send("requestFullscreen", false); }
        public void OnFatalError(int code) { Program.Send("fatalError", code); }
        public void OnWarning(int code) { Program.Send("warning", code); }
        public void OnRequestContainerMinimize() { Program.Send("requestMinimize", null); }
        public void OnLogonError(int err) { Program.Send("logonError", err); }
        public void OnFocusReleased(int dir) { Program.Send("focusReleased", dir); }
        public void OnAutoReconnected() { Program.Send("autoReconnected", null); }
        public void OnAutoReconnecting2(int reason, bool net, int attempt, int max)
        {
            Program.Send("autoReconnecting", new Dictionary<string, object> { { "reason", reason }, { "attempt", attempt }, { "max", max } });
        }
    }

    public class SessionForm : Form
    {
        // Newest first. MsRdpClient12 is registered on some builds but cannot be created there (CLASS_E_CLASSNOTAVAILABLE).
        static readonly string[] Clsids = {
            "3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a", // MsRdpClient12NotSafeForScripting
            "1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8", // MsRdpClient11NotSafeForScripting
            "a0c63c30-f08d-4ab4-907c-34905d770c7d", // MsRdpClient10NotSafeForScripting
            "8b918b82-7985-4c24-89df-c33ad2bbfbcd"  // MsRdpClient9NotSafeForScripting
        };
        readonly int parentPid;
        RdpAxHost ax;
        dynamic rdp;
        bool loggedIn;
        bool connectStarted;
        System.Windows.Forms.Timer resizeTimer;
        int pendingW, pendingH, pendingScale;

        public SessionForm(int parentPid)
        {
            this.parentPid = parentPid;
            Text = "BearingPoint Remote Desktop session";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 32, 32);
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1024, 768);
            foreach (var clsid in Clsids)
            {
                RdpAxHost a = null;
                try
                {
                    a = new RdpAxHost(clsid) { Dock = DockStyle.Fill };
                    a.Sink = new EventSink(this);
                    ((ISupportInitialize)a).BeginInit(); Controls.Add(a); ((ISupportInitialize)a).EndInit();
                    a.CreateControl();
                    if (a.Ocx == null) throw new Exception("no control");
                    ax = a; rdp = a.Ocx;
                    Program.Send("ready", new Dictionary<string, object> { { "clsid", clsid }, { "version", (string)rdp.Version } });
                    break;
                }
                catch (Exception ex)
                {
                    if (a != null) { Controls.Remove(a); a.Dispose(); }
                    Program.Send("debug", "clsid " + clsid + " failed: " + ex.Message);
                }
            }
            if (ax == null) { Program.Send("fatal", "The Remote Desktop ActiveX control is not available."); Environment.Exit(3); }
            // Resolution changes are sent to the server at most every 250 ms (the server rejects bursts).
            resizeTimer = new System.Windows.Forms.Timer { Interval = 250 };
            resizeTimer.Tick += delegate { resizeTimer.Stop(); ApplyResize(); };
        }

        // A tool window: no taskbar button and no hidden owner window.
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW
        }

        // Fields are validated in the main process; the helper checks them again and accepts nothing else.
        public void Connect(Dictionary<string, object> c)
        {
            if (connectStarted) { Program.Send("error", "already connecting"); return; }
            string host = Str(c, "host");
            if (!InputCheck.Host(host)) { Program.Send("error", "invalid host"); return; }
            string user = Str(c, "username");
            if (user != null && !InputCheck.User(user)) { Program.Send("error", "invalid username"); return; }
            string gw = Str(c, "gateway");
            if (!string.IsNullOrEmpty(gw) && !InputCheck.Host(gw)) { Program.Send("error", "invalid gateway"); return; }
            connectStarted = true;

            rdp.Server = host;
            if (!string.IsNullOrEmpty(user)) rdp.UserName = user;
            int w = Math.Max(200, Math.Min(8192, Int(c, "width", ClientSize.Width)));
            int h = Math.Max(200, Math.Min(8192, Int(c, "height", ClientSize.Height)));
            rdp.DesktopWidth = w; rdp.DesktopHeight = h;
            rdp.ColorDepth = 32;

            dynamic adv = rdp.AdvancedSettings9;
            adv.RDPPort = InputCheck.Port(Int(c, "port", 3389));
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
            string audio = Str(c, "audio");
            adv.AudioRedirectionMode = audio == "remote" ? 1 : audio == "none" ? 2 : 0;
            adv.AudioCaptureRedirectionMode = Bool(c, "microphone");
            rdp.SecuredSettings3.KeyboardHookMode = Bool(c, "keysToRemote") ? 1 : 2;

            var ns = (IMsRdpClientNonScriptable5)ax.Ocx;
            ns.set_ShowRedirectionWarningDialog(false);
            ns.set_AllowPromptingForCredentials(true);
            ns.set_AllowCredentialSaving(Bool(c, "allowCredentialSaving"));
            ns.set_NegotiateSecurityLayer(true);
            IntPtr top = Native.GetAncestor(Handle, Native.GA_ROOT);
            if (top != IntPtr.Zero && top != Handle) ns.set_UIParentWindowHandle(top);

            var ext = (IMsRdpExtendedSettings)ax.Ocx;
            SetExt(ext, "AllowAxToContainerEvents", true); // Ctrl+Alt+Left/Right gives the focus back to the app
            int scale = Int(c, "scale", 100);
            if (scale >= 100 && scale <= 500) { SetExt(ext, "DesktopScaleFactor", (uint)scale); SetExt(ext, "DeviceScaleFactor", (uint)100); }
            string protection = Str(c, "credentialProtection");
            if (protection == "remoteGuard") { SetExt(ext, "RedirectedAuthentication", true); SetExt(ext, "DisableCredentialsDelegation", true); }
            if (protection == "restrictedAdmin") { SetExt(ext, "RestrictedLogon", true); SetExt(ext, "DisableCredentialsDelegation", true); }

            if (!string.IsNullOrEmpty(gw))
            {
                dynamic ts = rdp.TransportSettings4;
                ts.GatewayHostname = gw;
                ts.GatewayUsageMethod = Str(c, "gatewayMode") == "detect" ? 2 : 1;
                ts.GatewayProfileUsageMethod = 1;
                ts.GatewayCredsSource = 4;
                ts.GatewayCredSharing = 1;
            }
            rdp.Connect(); // No password: CredSSP uses a saved TERMSRV/<host> credential or Windows asks.
        }

        static void SetExt(IMsRdpExtendedSettings e, string name, object v)
        {
            try { e.set_Property(name, ref v); } catch (Exception ex) { Program.Send("debug", "setting " + name + ": " + ex.Message); }
        }

        public void OnLoginComplete()
        {
            loggedIn = true;
            Program.Send("loginComplete", null);
            if (pendingW > 0) resizeTimer.Start();
        }

        public void HandleDisconnected(int reason)
        {
            string text = ""; int ext = 0;
            try { ext = (int)rdp.ExtendedDisconnectReason; text = (string)rdp.GetErrorDescription((uint)reason, (uint)ext); } catch { }
            Program.Send("disconnected", new Dictionary<string, object> { { "reason", reason }, { "extended", ext }, { "text", text } });
            BeginInvoke((Action)(() => Application.Exit()));
        }

        // Places the session window inside a window of the parent process (client coordinates, physical pixels),
        // or makes it a hidden top-level window again (parent 0).
        public void Attach(long parent, int x, int y, int w, int h, bool visible)
        {
            IntPtr hwnd = Handle;
            IntPtr p = new IntPtr(parent);
            if (parent != 0)
            {
                uint owner;
                Native.GetWindowThreadProcessId(p, out owner);
                if (owner != (uint)parentPid) { Program.Send("error", "parent window does not belong to the app"); return; }
            }
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
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
            if (parent != 0 && connectStarted)
            {
                try { ((IMsRdpClientNonScriptable5)ax.Ocx).set_UIParentWindowHandle(Native.GetAncestor(hwnd, Native.GA_ROOT)); } catch { }
            }
        }

        public void SetVisible(bool visible)
        {
            Native.ShowWindow(Handle, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
        }

        public void FocusSession()
        {
            Native.SetFocus(ax.Handle);
        }

        public void ResizeSession(int w, int h, int scale)
        {
            pendingW = w; pendingH = h; pendingScale = scale;
            if (!loggedIn) return;
            resizeTimer.Stop(); resizeTimer.Start();
        }

        void ApplyResize()
        {
            try
            {
                if ((short)rdp.Connected != 1) return;
                int w = Math.Max(200, Math.Min(8192, pendingW)) & ~1; // even width, as the protocol expects
                int h = Math.Max(200, Math.Min(8192, pendingH));
                int s = pendingScale >= 100 && pendingScale <= 500 ? pendingScale : 100;
                rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, (uint)s, 100u);
            }
            catch (Exception ex) { Program.Send("debug", "resize: " + ex.Message); }
        }

        public void DisconnectSession()
        {
            try { if ((short)rdp.Connected != 0) { rdp.Disconnect(); return; } } catch { }
            Application.Exit();
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : null; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static int Int(Dictionary<string, object> d, string k, int fallback) { object v; return d.TryGetValue(k, out v) && v is int ? (int)v : fallback; }
    }

    static class InputCheck
    {
        public static bool Host(string h)
        {
            if (string.IsNullOrEmpty(h) || h.Length > 253 || h[0] == '-' || h[0] == '.') return false;
            foreach (char ch in h)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '_' || ch == ':')) return false;
            return true;
        }
        public static bool User(string u)
        {
            if (u.Length > 256) return false;
            foreach (char ch in u) if (ch < 32 || ch == '"' || ch == 127) return false;
            return true;
        }
        public static int Port(int p) { if (p < 1 || p > 65535) throw new ArgumentOutOfRangeException("port"); return p; }
    }

    static class Native
    {
        public const int GWL_STYLE = -16;
        public const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CLIPSIBLINGS = 0x04000000L, WS_CLIPCHILDREN = 0x02000000L;
        public const uint SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
        public const int SW_HIDE = 0, SW_SHOWNA = 8;
        public const uint GA_ROOT = 2;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    }

    static class Program
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object OutLock = new object();
        static SessionForm form;
        // A winexe has no console: wrap the raw standard handles (Console.InputEncoding would throw).
        static readonly System.IO.TextReader In = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        static readonly System.IO.TextWriter Out = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        public static void Send(string type, object payload)
        {
            string line = Json.Serialize(new Dictionary<string, object> { { "type", type }, { "data", payload } });
            lock (OutLock) { try { Out.WriteLine(line); } catch { } }
        }

        [STAThread]
        static int Main(string[] args)
        {
            int parentPid = 0;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--parent-pid") int.TryParse(args[i + 1], out parentPid);
            if (parentPid <= 0) return 2;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2, like Electron
            Application.EnableVisualStyles();
            form = new SessionForm(parentPid);
            var reader = new Thread(ReadLoop) { IsBackground = true };
            form.Load += delegate { reader.Start(); };
            Application.Run(form);
            return 0;
        }

        static void ReadLoop()
        {
            string line;
            while ((line = In.ReadLine()) != null)
            {
                if (line.Length > 16384) { Send("error", "line too long"); continue; }
                Dictionary<string, object> msg;
                try { msg = Json.Deserialize<Dictionary<string, object>>(line); } catch { Send("error", "bad json"); continue; }
                if (msg == null) continue;
                try { form.BeginInvoke((Action)(() => Dispatch(msg))); } catch { return; }
            }
            // The app is gone: end the session and exit.
            try { form.BeginInvoke((Action)(() => { form.DisconnectSession(); Application.Exit(); })); } catch { }
        }

        static void Dispatch(Dictionary<string, object> m)
        {
            try
            {
                object cmdObj; m.TryGetValue("cmd", out cmdObj);
                object argsObj; m.TryGetValue("args", out argsObj);
                var a = argsObj as Dictionary<string, object> ?? new Dictionary<string, object>();
                switch (cmdObj as string)
                {
                    case "connect": form.Connect(a); break;
                    case "attach":
                        form.Attach(Convert.ToInt64(a["parent"]), Convert.ToInt32(a["x"]), Convert.ToInt32(a["y"]), Convert.ToInt32(a["w"]), Convert.ToInt32(a["h"]), a.ContainsKey("visible") && a["visible"] is bool && (bool)a["visible"]);
                        break;
                    case "visible": form.SetVisible(a.ContainsKey("value") && a["value"] is bool && (bool)a["value"]); break;
                    case "focus": form.FocusSession(); break;
                    case "resize": form.ResizeSession(Convert.ToInt32(a["w"]), Convert.ToInt32(a["h"]), Convert.ToInt32(a["scale"])); break;
                    case "disconnect": form.DisconnectSession(); break;
                    default: Send("error", "unknown command"); break; // deliberately no generic property setter
                }
            }
            catch (Exception ex) { Send("error", ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
