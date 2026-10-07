using System.Collections.ObjectModel;
using System.Windows.Input;
using RdpManager.Core.Models;
using RdpManager.Core.Targets;
using RdpManager.Core.Validation;

namespace RdpManager.App.ViewModels;

/// <summary>Display, device and gateway/security options of a system (shared by the edit and connect dialogs).</summary>
public sealed class OptionsViewModel : ObservableObject
{
    public static readonly (int W, int H)[] Resolutions = [(1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160)];
    private readonly PolicyRedirect? _lock;
    private string _mode;
    private string _resolution;
    private string _gatewayMode;
    private string _gatewayHost;
    private string _gatewayError = "";

    public OptionsViewModel(Connection c, AppPolicy? policy, bool tabsMode)
    {
        TabsMode = tabsMode;
        _lock = policy?.Redirect;
        RequiredProtection = policy?.RequireCredentialProtection;
        var d = c.Display ?? new DisplayOptions();
        var r = c.Redirect ?? new RedirectOptions();
        var g = c.Gateway ?? new GatewayOptions();
        var s = c.Security ?? new SecurityOptions();
        _mode = d.Mode;
        _resolution = $"{d.Width}x{d.Height}";
        ResolutionOptions = Resolutions.Select(x => new KeyValuePair<string, string>($"{x.W}x{x.H}", $"{x.W} × {x.H}")).ToList();
        if (ResolutionOptions.All(o => o.Key != _resolution)) ResolutionOptions.Insert(0, new(_resolution, $"{d.Width} × {d.Height}"));
        Multimon = d.Multimon;
        DynamicResolution = d.DynamicResolution;
        SmartSizing = d.SmartSizing;
        Clipboard = _lock?.Clipboard ?? r.Clipboard;
        Drives = (_lock?.Drives ?? r.Drives) == "all";
        Printers = _lock?.Printers ?? r.Printers;
        Microphone = _lock?.Microphone ?? r.Microphone;
        Smartcards = _lock?.Smartcards ?? r.Smartcards;
        Audio = _lock?.Audio ?? r.Audio;
        _gatewayMode = g.Mode;
        _gatewayHost = g.Host;
        CredentialProtection = s.CredentialProtection;
        AuthLevel = s.AuthLevel == 1 ? 1 : 2;
        AdminSession = s.AdminSession;
    }

    public bool TabsMode { get; }
    public bool ShowWindowOptions => !TabsMode;
    public string DisplayMode { get => _mode; set { if (Set(ref _mode, value)) OnPropertyChanged(nameof(IsWindow)); } }
    public bool IsWindow => _mode == "window";
    public List<KeyValuePair<string, string>> ResolutionOptions { get; }
    public string Resolution { get => _resolution; set => Set(ref _resolution, value); }
    public bool Multimon { get; set; }
    public bool DynamicResolution { get; set; }
    public bool SmartSizing { get; set; }
    public string MultimonHelp => TabsMode
        ? "The remote desktop spans all monitors. Such a system opens in a separate Remote Desktop window, because a tab covers one monitor."
        : "The remote desktop spans all monitors of this computer.";

    public bool Clipboard { get; set; }
    public bool Drives { get; set; }
    public bool Printers { get; set; }
    public bool Microphone { get; set; }
    public bool Smartcards { get; set; }
    public string Audio { get; set; }
    public bool ClipboardLocked => _lock?.Clipboard is not null;
    public bool DrivesLocked => _lock?.Drives is not null;
    public bool PrintersLocked => _lock?.Printers is not null;
    public bool MicrophoneLocked => _lock?.Microphone is not null;
    public bool SmartcardsLocked => _lock?.Smartcards is not null;
    public bool AudioLocked => _lock?.Audio is not null;

    public string GatewayMode { get => _gatewayMode; set { if (Set(ref _gatewayMode, value)) OnPropertyChanged(nameof(UsesGateway)); } }
    public bool UsesGateway => _gatewayMode != "none";
    public string GatewayHost { get => _gatewayHost; set { if (Set(ref _gatewayHost, value)) GatewayError = ""; } }
    public string GatewayError { get => _gatewayError; set { if (Set(ref _gatewayError, value)) OnPropertyChanged(nameof(HasGatewayError)); } }
    public bool HasGatewayError => _gatewayError.Length > 0;
    public string CredentialProtection { get; set; }
    public string? RequiredProtection { get; }
    public string ProtectionHelp => "Remote Credential Guard keeps your password on this computer. It requires Kerberos and support on the remote system."
        + (RequiredProtection is null ? "" : $" IT policy requires {RequiredProtection}.");
    public int AuthLevel { get; set; }
    public bool AdminSession { get; set; }

    public static IReadOnlyList<KeyValuePair<string, string>> ModeOptions { get; } = [new("fullscreen", "Full screen"), new("window", "Window")];
    public static IReadOnlyList<KeyValuePair<string, string>> AudioOptions { get; } = [new("local", "Play on this computer"), new("remote", "Play on the remote computer"), new("none", "Do not play")];
    public static IReadOnlyList<KeyValuePair<string, string>> GatewayChoices { get; } = [new("none", "Do not use a gateway"), new("always", "Always use this gateway"), new("detect", "Use only if direct connection fails")];
    public static IReadOnlyList<KeyValuePair<string, string>> ProtectionOptions { get; } = [new("none", "Standard sign-in"), new("remoteGuard", "Remote Credential Guard (recommended for admin access)"), new("restrictedAdmin", "Restricted Admin mode")];
    public static IReadOnlyList<KeyValuePair<int, string>> AuthOptions { get; } = [new(2, "Warn me and let me decide"), new(1, "Do not connect")];

    /// <summary>Validates the gateway (shown next to the field). Returns false when the dialog must stay open.</summary>
    public bool Validate()
    {
        if (UsesGateway && GatewayHost.Trim().Length == 0) { GatewayError = "Enter the gateway address or switch the gateway off."; return false; }
        return true;
    }

    public (DisplayOptions, RedirectOptions, GatewayOptions, SecurityOptions) Read()
    {
        var parts = _resolution.Split('x');
        var w = int.TryParse(parts[0], out var pw) ? pw : 1920;
        var h = parts.Length > 1 && int.TryParse(parts[1], out var ph) ? ph : 1080;
        return (
            new DisplayOptions { Mode = _mode, Width = w, Height = h, Multimon = Multimon, DynamicResolution = DynamicResolution, SmartSizing = SmartSizing },
            new RedirectOptions { Clipboard = Clipboard, Drives = Drives ? "all" : "none", Printers = Printers, Audio = Audio, Microphone = Microphone, Smartcards = Smartcards },
            new GatewayOptions { Mode = _gatewayMode, Host = _gatewayHost.Trim() },
            new SecurityOptions { CredentialProtection = CredentialProtection, AuthLevel = AuthLevel, AdminSession = AdminSession });
    }
}

/// <summary>Add or edit a system.</summary>
public sealed class EditSystemViewModel : ObservableObject
{
    private readonly Connection _c;
    private string _protocol;
    private string _host;
    private string _port;
    private bool _portTouched;
    private string _hostError = "";
    private string _formError = "";
    private string _detected = "";
    private string _username;
    private string _scheme;
    private string _webPath;

    public EditSystemViewModel(Connection? existing, AppSettings settings, AppPolicy? policy, IEnumerable<string> folders, string? savedCredentialUser)
    {
        IsNew = existing is null || string.IsNullOrEmpty(existing.Id);
        _c = existing?.Clone() ?? new Connection
        {
            Port = 3389,
            Display = settings.Defaults.Display.Clone(),
            Redirect = settings.Defaults.Redirect.Clone(),
            Gateway = settings.Defaults.Gateway.Clone(),
            Security = settings.Defaults.Security.Clone(),
        };
        _protocol = Protocols.Of(_c);
        _c.Ssh ??= new SshOptions();
        _c.Web ??= new WebOptions();
        var allowed = policy?.AllowedProtocols ?? Protocols.All;
        // Only switched-on types are offered; an existing system keeps its own type so it can still be edited.
        Types = Protocols.All.Where(p => (Protocols.Enabled.Contains(p) && allowed.Contains(p)) || p == _protocol).ToList();
        Title = IsNew ? "Add system" : $"Edit {existing!.Name}";
        _host = _c.Host;
        _port = (_c.Port == 0 ? Protocols.DefaultPort(_protocol, _c.Web.Scheme) : _c.Port).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _portTouched = !IsNew && _c.Port != Protocols.DefaultPort(_protocol, _c.Web.Scheme);
        Name = _c.Name;
        _username = _c.Username;
        Folder = _c.Folder;
        Os = _c.Os;
        Location = _c.Location;
        Tags = string.Join(", ", _c.Tags);
        Description = _c.Description;
        Favorite = _c.Favorite;
        KeyFile = _c.Ssh.IdentityFile;
        JumpHost = _c.Ssh.JumpHost;
        _scheme = _c.Web.Scheme;
        _webPath = _c.Web.Path;
        Folders = folders.Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Options = new OptionsViewModel(_c, policy, settings.SessionWindow != "external");
        AllowSavedPassword = policy?.AllowSavedCredentials != false && !_c.IsCentral;
        PasswordStatus = savedCredentialUser is null ? "Windows then signs you in automatically. Needs the username above." : $"A password for {savedCredentialUser} is saved. Type a new one to replace it.";
        AdvancedOpen = _c.Folder.Length > 0 || _c.Os.Length > 0 || _c.Location.Length > 0 || _c.Tags.Count > 0 || _c.Description.Length > 0;
    }

    public bool IsNew { get; }
    public string Title { get; }
    public string SaveLabel => IsNew ? "Add system" : "Save changes";
    public IReadOnlyList<string> Types { get; }
    public bool ShowTypes => Types.Count > 1;
    public OptionsViewModel Options { get; }
    public IReadOnlyList<string> Folders { get; }
    public IReadOnlyList<string> OsSuggestions { get; } = ["Windows Server 2025", "Windows Server 2022", "Windows Server 2019", "Windows 11 Enterprise", "Windows 10 Enterprise"];
    public bool AdvancedOpen { get; set; }

    public string Protocol
    {
        get => _protocol;
        set
        {
            if (!Set(ref _protocol, value)) return;
            if (!_portTouched) _port = Protocols.DefaultPort(value, _scheme).ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(Port));
            OnPropertyChanged(nameof(IsRdp)); OnPropertyChanged(nameof(IsSsh)); OnPropertyChanged(nameof(IsWeb)); OnPropertyChanged(nameof(NotWeb));
            OnPropertyChanged(nameof(HostLabel)); OnPropertyChanged(nameof(HostPlaceholder)); OnPropertyChanged(nameof(UserPlaceholder));
            OnPropertyChanged(nameof(Preview));
        }
    }

    public bool IsRdp => _protocol == Protocols.Rdp;
    public bool IsSsh => _protocol == Protocols.Ssh;
    public bool IsWeb => _protocol == Protocols.Web;
    public bool NotWeb => !IsWeb;
    public string HostLabel => (IsRdp ? "Computer name or IP address" : "Host name or IP address") + " (required)";
    public string HostPlaceholder => _protocol switch { Protocols.Ssh => "linux01.domain.local", Protocols.Web => "ilo01.domain.local", _ => "server01.domain.local" };
    public string UserPlaceholder => _protocol switch { Protocols.Ssh => "for example admin", Protocols.Web => "", _ => "DOMAIN\\username" };

    public string Host { get => _host; set { if (Set(ref _host, value)) { HostError = ""; OnPropertyChanged(nameof(Preview)); } } }
    public string Port { get => _port; set { if (Set(ref _port, value)) { _portTouched = true; OnPropertyChanged(nameof(Preview)); } } }
    public string Name { get; set; }
    public string Username { get => _username; set { if (Set(ref _username, value)) OnPropertyChanged(nameof(Preview)); } }
    public string Folder { get; set; }
    public string Os { get; set; }
    public string Location { get; set; }
    public string Tags { get; set; }
    public string Description { get; set; }
    public bool Favorite { get; set; }
    public string KeyFile { get; set; }
    public string JumpHost { get; set; }
    public string Scheme { get => _scheme; set { if (Set(ref _scheme, value)) { if (!_portTouched) { _port = Protocols.DefaultPort(Protocols.Web, value).ToString(System.Globalization.CultureInfo.InvariantCulture); OnPropertyChanged(nameof(Port)); } OnPropertyChanged(nameof(Preview)); } } }
    public string WebPath { get => _webPath; set { if (Set(ref _webPath, value)) OnPropertyChanged(nameof(Preview)); } }
    public bool AllowSavedPassword { get; }
    public string PasswordStatus { get; }

    public string HostError { get => _hostError; set { if (Set(ref _hostError, value)) OnPropertyChanged(nameof(HasHostError)); } }
    public bool HasHostError => _hostError.Length > 0;
    public string FormError { get => _formError; set { if (Set(ref _formError, value)) OnPropertyChanged(nameof(HasFormError)); } }
    public bool HasFormError => _formError.Length > 0;
    public string Detected { get => _detected; private set => Set(ref _detected, value); }

    private int PortNumber => int.TryParse(_port, out var p) ? p : 0;

    public string Preview => _host.Trim().Length == 0 ? ""
        : "Starts: " + HostRules.Describe(_protocol, _host.Trim(), PortNumber == 0 ? Protocols.DefaultPort(_protocol, _scheme) : PortNumber, _username.Trim(), new WebOptions { Scheme = _scheme, Path = _webPath.Trim() });

    /// <summary>A pasted full address ("ssh admin@host -p 2222", "https://ilo01/admin") fills the fields.</summary>
    public void DetectFromHost()
    {
        var raw = _host.Trim();
        if (Types.Count < 2 || !System.Text.RegularExpressions.Regex.IsMatch(raw, @"[@\s]|://|:\d+$")) return;
        var t = AddressParser.ParseTargets(raw).FirstOrDefault();
        if (t is null || !Types.Contains(t.Protocol)) return;
        Protocol = t.Protocol;
        Host = t.Host;
        if (t.Username.Length > 0) Username = t.Username;
        if (t.Protocol == Protocols.Web) { Scheme = t.Scheme; WebPath = t.Path; }
        _portTouched = t.Port != Protocols.DefaultPort(t.Protocol, t.Scheme);
        _port = t.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Port));
        Detected = $"Recognized as {(t.Protocol == Protocols.Rdp ? "Remote Desktop" : Protocols.Label(t.Protocol))}. The fields were filled in from the address.";
    }

    /// <summary>Builds the record to save, or returns null and shows the problem in the form.</summary>
    public Connection? BuildPayload(out bool selectGeneralTab)
    {
        selectGeneralTab = false;
        if (_host.Trim().Length == 0)
        {
            HostError = "Enter the computer name or IP address of the system.";
            selectGeneralTab = true;
            return null;
        }
        if (IsRdp && !Options.Validate()) return null;
        var (display, redirect, gateway, security) = Options.Read();
        var c = _c.Clone();
        c.Protocol = _protocol;
        c.Host = _host.Trim();
        c.Port = PortNumber == 0 ? Protocols.DefaultPort(_protocol, _scheme) : PortNumber;
        c.Name = Name.Trim();
        c.Username = IsWeb ? "" : _username.Trim();
        c.Folder = Folder.Trim();
        c.Os = Os.Trim();
        c.Location = Location.Trim();
        c.Tags = ConnectionNormalizer.ParseTags(Tags);
        c.Description = Description.Trim();
        c.Favorite = Favorite;
        c.Ssh = new SshOptions { IdentityFile = KeyFile.Trim(), JumpHost = JumpHost.Trim() };
        c.Web = new WebOptions { Scheme = _scheme, Path = _webPath.Trim() };
        c.Display = display;
        c.Redirect = redirect;
        // Gateway settings only apply to RDP; a hidden gateway must not block saving other types.
        c.Gateway = IsRdp ? gateway : new GatewayOptions();
        c.Security = security;
        if (IsNew) { c.Id = ""; c.Sample = false; c.LastConnectedAt = null; c.Source = null; }
        return c;
    }
}

/// <summary>"Connect with options": one-time options, a different user, optionally remembered.</summary>
public sealed class ConnectOptionsViewModel(Connection c, AppPolicy? policy, bool tabsMode) : ObservableObject
{
    private bool _promptAlways;
    private string _credentialText = "Checking Windows Credential Manager…";

    public Connection Connection { get; } = c;
    public string Title => $"Connect to {Connection.Name}";
    public string Address => Connection.Port is 0 or 3389 ? Connection.Host : $"{Connection.Host}:{Connection.Port}";
    public string Username { get; set; } = c.Username;
    public OptionsViewModel Options { get; } = new(c, policy, tabsMode);
    public bool PromptAlways { get => _promptAlways; set { if (Set(ref _promptAlways, value)) OnPropertyChanged(nameof(UsernameEnabled)); } }
    public bool UsernameEnabled => !_promptAlways;
    public bool Remember { get; set; }
    public string CredentialText { get => _credentialText; set => Set(ref _credentialText, value); }
}

/// <summary>One entry in the quick connect list: a typed address or a saved system.</summary>
public sealed record QuickOption(ParsedTarget? Target, ConnectionItem? System, string Title, string Hint, string Icon)
{
    public bool IsAddress => Target is not null;
}

public sealed class QuickConnectViewModel : ObservableObject
{
    private readonly IReadOnlyList<ConnectionItem> _items;
    private readonly Func<string, bool> _protocolAllowed;
    private string _text = "";
    private int _index;

    public QuickConnectViewModel(IReadOnlyList<ConnectionItem> items, Func<string, bool> protocolAllowed, string prefill)
    {
        _items = items;
        _protocolAllowed = protocolAllowed;
        _text = prefill;
        Update();
    }

    public bool ShowTypes => Protocols.Enabled.Count > 1;
    public string Subtitle => ShowTypes ? "Enter an address to connect right away, or pick a saved system." : "Enter a computer name or IP address to connect right away, or pick a saved system.";
    public string Placeholder => ShowTypes ? "server01, ssh admin@linux01 or https://ilo01" : "for example server01.corp.local, 10.20.30.40 or 10.20.30.40:3390";
    public string Text { get => _text; set { if (Set(ref _text, value)) { _index = 0; Update(); } } }
    public bool Save { get; set; }
    public ObservableCollection<QuickOption> Options { get; } = [];
    public string EmptyText { get; private set; } = "";
    public bool IsEmpty => Options.Count == 0;
    public int Index { get => _index; set { if (Set(ref _index, Math.Clamp(value, 0, Math.Max(0, Options.Count - 1)))) OnPropertyChanged(nameof(Selected)); } }
    public QuickOption? Selected => Options.Count > 0 ? Options[Math.Min(_index, Options.Count - 1)] : null;

    private static readonly Dictionary<string, string> Hints = new()
    {
        [Protocols.Rdp] = "Windows asks for your username and password",
        [Protocols.Ssh] = "Opens a terminal window. ssh asks for your password or uses your key",
        [Protocols.Web] = "Opens in your default browser",
    };

    private void Update()
    {
        var raw = _text.Trim();
        var q = raw.ToLowerInvariant();
        var matches = _items
            .OrderByDescending(c => c.Model.LastConnectedAt ?? "", StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(c => q.Length == 0 || $"{c.Name} {c.Host} {c.Address} {string.Join(' ', c.Model.Tags)}".Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(7).ToList();
        Options.Clear();
        foreach (var t in AddressParser.EnabledTargets(raw))
        {
            if (!_protocolAllowed(t.Protocol)) continue;
            if (matches.Any(c => c.Protocol == t.Protocol && string.Equals(c.Host, t.Host, StringComparison.OrdinalIgnoreCase) && c.Model.Port == t.Port)) continue;
            var title = ShowTypes ? HostRules.Describe(t.Protocol, t.Host, t.Port, t.Username, new WebOptions { Scheme = t.Scheme, Path = t.Path })
                : $"Connect to {t.Host}{(t.Port != 3389 ? $":{t.Port}" : "")}";
            Options.Add(new QuickOption(t, null, title, Hints[t.Protocol], "connect"));
        }
        foreach (var c in matches) Options.Add(new QuickOption(null, c, c.Name, c.Address, c.CardIcon));
        EmptyText = raw.Length > 0 ? "Not a valid address, and no saved system matches." : ShowTypes ? "Type a computer name, IP address or web address." : "Type a computer name or IP address.";
        _index = Math.Min(_index, Math.Max(0, Options.Count - 1));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(Selected));
    }
}

/// <summary>Steps of the connection progress dialog.</summary>
public sealed class StepItem(string key, string label) : ObservableObject
{
    private string _state = "pending";
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class ConnectProgressViewModel : ObservableObject
{
    private string? _noteTitle;
    private string? _noteMessage;
    private string _noteKind = "info";
    private string? _technical;
    private string? _extra;

    public ConnectProgressViewModel(Connection c)
    {
        Name = c.Name;
        Host = c.Host;
        IReadOnlyList<(string, string)> steps = Protocols.Of(c) == Protocols.Ssh
            ? [("check", "Checking system availability"), ("validate", "Checking the SSH client and settings"), ("start", "Opening the terminal window")]
            : [("check", "Checking system availability"), ("validate", "Validating connection settings"), ("secure", "Preparing secure sign-in"), ("start", "Starting Remote Desktop"), ("session", "Waiting for the remote session")];
        Steps = steps.Select(s => new StepItem(s.Item1, s.Item2)).ToList();
    }

    public string Name { get; }
    public string Host { get; }
    public IReadOnlyList<StepItem> Steps { get; }

    public void SetStep(string key, string state)
    {
        if (Steps.FirstOrDefault(s => s.Key == key) is { } s) s.State = state;
    }

    public string NoteKind { get => _noteKind; private set => Set(ref _noteKind, value); }
    public string? NoteTitle { get => _noteTitle; private set { if (Set(ref _noteTitle, value)) OnPropertyChanged(nameof(HasNote)); } }
    public string? NoteMessage { get => _noteMessage; private set => Set(ref _noteMessage, value); }
    public string? Technical { get => _technical; private set { if (Set(ref _technical, value)) OnPropertyChanged(nameof(HasTechnical)); } }
    public string? Extra { get => _extra; set { if (Set(ref _extra, value)) OnPropertyChanged(nameof(HasExtra)); } }
    public bool HasNote => _noteTitle is not null;
    public bool HasTechnical => _technical is not null;
    public bool HasExtra => _extra is not null;
    public string NoteIcon => _noteKind switch { "error" => "xCircle", "warning" => "warning", _ => "info" };

    public void Note(string kind, string title, string message, string? technical = null)
    {
        NoteKind = kind;
        NoteTitle = title;
        NoteMessage = message;
        Technical = technical;
        OnPropertyChanged(nameof(NoteIcon));
    }
}

public sealed record ImportPreviewRow(string Kind, string Title, string Detail, string Permissions, IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
    public string Icon => Kind switch { "error" => "xCircle", "notice" => "info", _ => "file" };
}

public sealed record CommandItem(string Label, ICommand Command, object? Parameter = null);
