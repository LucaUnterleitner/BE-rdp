using System.Windows.Input;
using RdpManager.Core.Formatting;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Rdp;
using RdpManager.Core.Search;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Storage;
using RdpManager.Infrastructure.Windows;

namespace RdpManager.App.ViewModels;

public sealed record KeyValue(string Key, string Value, bool Mono = false, string? Note = null);

public sealed record ErrorCodeRow(string Code, string Title, string Message);

/// <summary>System details (opened as a tab next to Home).</summary>
public sealed class DetailsPageViewModel : PageViewModel
{
    private string _credentialText = "Checking…";
    private bool _credentialSaved;
    private bool _checking;

    public DetailsPageViewModel(MainViewModel main, string id) : base(main)
    {
        Id = id;
        ProbeCommand = new AsyncCommand(async () =>
        {
            Checking = true;
            try { await Main.Controller!.ProbeAsync(Id); }
            finally { Checking = false; }
        });
        SaveCredentialCommand = new RelayCommand(() => Main.Flows?.SaveCredential(Item, LoadCredential));
        RemoveCredentialCommand = new AsyncCommand(() => Main.Flows?.RemoveCredentialAsync(Item, LoadCredential) ?? Task.CompletedTask);
        LoadCredential();
    }

    public override string Route => "details";
    public override string? RouteId => Id;
    public string Id { get; }
    public ConnectionItem Item => Main.ItemById(Id)!;
    public ICommand ProbeCommand { get; }
    public ICommand SaveCredentialCommand { get; }
    public ICommand RemoveCredentialCommand { get; }
    public bool Checking { get => _checking; private set => Set(ref _checking, value); }

    public IReadOnlyList<MainViewModel.Banner> Banners => Main.Banners;
    public bool IsRdp => Item.IsRdp;
    public bool IsSsh => Item.Protocol == Protocols.Ssh;
    public bool IsWeb => Item.Protocol == Protocols.Web;
    public bool CanEdit => !Item.IsCentral;
    public bool ShowTypeBadge => Protocols.Enabled.Count > 1 || !Item.IsRdp;

    public bool IsOffline => Item.StatusKey == "offline";
    public string OfflineMessage => $"{(SystemFilter.ReasonText(Item.Protocol, Item.Status.Reason) is { Length: > 0 } r ? r : "No response")}. Check that the system is online and that the VPN is active. Last checked {Format.Time(Item.Probe?.CheckedAt)}.";

    public void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(OfflineMessage));
        OnPropertyChanged(nameof(Item));
    }

    public IReadOnlyList<KeyValue> SystemFacts
    {
        get
        {
            var c = Item.Model;
            var list = new List<KeyValue>
            {
                new(IsWeb ? "Address" : "Computer name", Item.Address, true),
                new("Operating system", c.Os.Length > 0 ? c.Os : "–"),
                new("Location", c.Location.Length > 0 ? c.Location : "–"),
                new("Group", c.Folder.Length > 0 ? c.Folder : "–"),
                new("Tags", c.Tags.Count > 0 ? string.Join(", ", c.Tags) : "–"),
                new("Last used", c.LastConnectedAt is null ? "Never" : Format.When(c.LastConnectedAt)),
            };
            if (c.Description.Length > 0) list.Add(new("Description", c.Description));
            return list;
        }
    }

    public string UsernameText => Item.Model.Username.Length > 0 ? Item.Model.Username : "Suggested by Windows";
    public string CredentialText { get => _credentialText; private set => Set(ref _credentialText, value); }
    public bool CredentialSaved { get => _credentialSaved; private set { if (Set(ref _credentialSaved, value)) OnPropertyChanged(nameof(CanSaveCredential)); } }
    public bool AllowSavedCredentials => Main.Policy?.AllowSavedCredentials != false;
    public bool CanSaveCredential => AllowSavedCredentials && !CredentialSaved;

    public async void LoadCredential()
    {
        if (!IsRdp) return;
        try
        {
            var cred = await Task.Run(() => Main.Controller!.GetCredential(Id));
            CredentialSaved = cred.Saved;
            CredentialText = cred.Saved ? $"Saved for {cred.Username} in Windows Credential Manager" : "Not saved. Windows asks when you connect.";
        }
        catch (InvalidOperationException)
        {
            CredentialText = "Windows Credential Manager is not available.";
        }
    }

    public IReadOnlyList<KeyValue> Options
    {
        get
        {
            var c = Item.Model;
            var p = Main.Policy?.Redirect;
            var r = c.Redirect ?? new RedirectOptions();
            string OnOff(bool b) => b ? "On" : "Off";
            string? Lock(string k) => p?.IsLocked(k) == true ? "(IT policy)" : null;
            var clip = p?.Clipboard ?? r.Clipboard;
            var drives = (p?.Drives ?? r.Drives) == "all";
            var printers = p?.Printers ?? r.Printers;
            var mic = p?.Microphone ?? r.Microphone;
            var cards = p?.Smartcards ?? r.Smartcards;
            var audio = (p?.Audio ?? r.Audio) switch { "remote" => "Play on remote computer", "none" => "Do not play", _ => "Play on this computer" };
            var d = c.Display ?? new DisplayOptions();
            var tabs = Main.Controller!.Store.Settings.SessionWindow != "external";
            var display = d.Multimon ? "All monitors, separate window" : tabs ? "Tab in the app" : d.Mode == "window" ? $"Window {d.Width} × {d.Height}" : "Full screen";
            var g = c.Gateway ?? new GatewayOptions();
            var prot = (c.Security?.CredentialProtection) switch { "remoteGuard" => "Remote Credential Guard", "restrictedAdmin" => "Restricted Admin mode", _ => "Standard sign-in" };
            return
            [
                new("Display", display),
                new("Clipboard", OnOff(clip), Note: Lock("clipboard")),
                new("Local drives", OnOff(drives), Note: Lock("drives")),
                new("Printers", OnOff(printers), Note: Lock("printers")),
                new("Audio", audio, Note: Lock("audio")),
                new("Microphone", OnOff(mic), Note: Lock("microphone")),
                new("Smart cards", OnOff(cards), Note: Lock("smartcards")),
                new("RD Gateway", g.Mode == "none" ? "Not used" : $"{g.Host} ({(g.Mode == "always" ? "always" : "if needed")})"),
                new("Credentials", prot),
                new("Admin session", OnOff(c.Security?.AdminSession == true)),
            ];
        }
    }

    public IReadOnlyList<KeyValue> SshFacts =>
    [
        new("Username", Item.Model.Username.Length > 0 ? Item.Model.Username : "Asked in the terminal"),
        new("Sign-in", Item.Model.Ssh?.IdentityFile is { Length: > 0 } k ? $"Key file {k}" : "Password or key, asked in the terminal"),
        new("Jump host", Item.Model.Ssh?.JumpHost is { Length: > 0 } j ? j : "Not used"),
    ];

    public IReadOnlyList<HistoryRow> History => Main.HistoryEvents().Where(e => e.ConnectionId == Id).Take(10).ToList() is var list
        ? list.Select((e, i) => new HistoryRow(e, Item, i == list.Count - 1, Main.ConnectCommand, Main.QuickHostCommand)).ToList()
        : [];

    public override void Refresh() => OnAllPropertiesChanged();
}

/// <summary>Settings form. Values are edited on a copy and saved together.</summary>
public sealed class SettingsPageViewModel : PageViewModel
{
    private readonly AppSettings _s;

    public SettingsPageViewModel(MainViewModel main) : base(main)
    {
        _s = main.Controller!.Store.Settings;
        var launch = main.Controller.EffectiveLaunch(_s);
        _launchMode = launch.LaunchMode;
        SaveCommand = new AsyncCommand(SaveAsync);
        RerunMigrationCommand = new AsyncCommand(() => Main.Flows?.RerunMigrationAsync() ?? Task.CompletedTask);
        OpenLogsCommand = new RelayCommand(() => Main.Flows?.OpenFolder(Main.Controller!.Paths.LogsDir));
        _ = LoadStartupStateAsync();
        Migration = JsonFile.TryRead(main.Controller.Paths.MigrationFile, RdpJsonContext.Default.MigrationRecord);
    }

    public override string Route => "settings";
    public ICommand SaveCommand { get; }
    public ICommand RerunMigrationCommand { get; }
    public ICommand OpenLogsCommand { get; }

    public bool Tabs { get => _s.SessionWindow != "external"; set { _s.SessionWindow = value ? "tabs" : "external"; OnPropertyChanged(); OnPropertyChanged(nameof(External)); OnPropertyChanged(nameof(ShowDisplayMode)); } }
    public bool External { get => !Tabs; set => Tabs = !value; }
    public bool KeysToRemote { get => _s.KeysToRemote; set => _s.KeysToRemote = value; }

    private string _launchMode;
    public bool LaunchLocked => Main.Policy?.LaunchMode is not null || LaunchNote is not null;
    public string? LaunchNote => Main.Policy?.LaunchMode is not null ? "The launch mode is set by IT policy." : Main.Controller!.EffectiveLaunch(_s).Note;
    public bool LaunchFile { get => _launchMode == "file"; set { if (value) { _launchMode = "file"; OnPropertyChanged(nameof(LaunchDirect)); } } }
    public bool LaunchDirect { get => _launchMode == "direct"; set { if (value) { _launchMode = "direct"; OnPropertyChanged(nameof(LaunchFile)); } } }
    public bool ThumbprintLocked => Main.Policy?.SigningThumbprint is not null;
    public string Thumbprint { get => Main.Policy?.SigningThumbprint ?? _s.SigningThumbprint; set => _s.SigningThumbprint = value; }

    public bool ShowDisplayMode => External;
    public int DisplayModeIndex { get => _s.Defaults.Display.Mode == "window" ? 1 : 0; set => _s.Defaults.Display.Mode = value == 1 ? "window" : "fullscreen"; }
    public bool DefMultimon { get => _s.Defaults.Display.Multimon; set => _s.Defaults.Display.Multimon = value; }
    public bool DefClipboard { get => _s.Defaults.Redirect.Clipboard; set => _s.Defaults.Redirect.Clipboard = value; }
    public bool DefPrinters { get => _s.Defaults.Redirect.Printers; set => _s.Defaults.Redirect.Printers = value; }
    public bool DefDrives { get => _s.Defaults.Redirect.Drives == "all"; set => _s.Defaults.Redirect.Drives = value ? "all" : "none"; }

    public IReadOnlyList<KeyValuePair<int, string>> RefreshOptions { get; } =
        SettingsNormalizer.RefreshChoices.Select(v => new KeyValuePair<int, string>(v, v < 60 ? $"{v} seconds" : v == 60 ? "1 minute" : $"{v / 60} minutes")).ToList();
    public int StatusRefreshSeconds { get => _s.StatusRefreshSeconds; set => _s.StatusRefreshSeconds = value; }
    public bool ConfirmDisconnect { get => _s.ConfirmDisconnect; set => _s.ConfirmDisconnect = value; }
    public bool KeepRunningInTray { get => _s.KeepRunningInTray; set => _s.KeepRunningInTray = value; }
    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    private async Task LoadStartupStateAsync()
    {
        StartWithWindows = await PackageInfo.IsStartWithWindowsEnabledAsync();
    }

    private async Task SaveAsync()
    {
        // Start from the current settings and copy only what this page edits: view preferences changed elsewhere
        // since the page opened stay, and values forced by IT policy are not stored as the user's own choice.
        var current = Main.Controller!.Store.Settings;
        current.SessionWindow = _s.SessionWindow;
        current.KeysToRemote = _s.KeysToRemote;
        if (!LaunchLocked) current.LaunchMode = _launchMode;
        if (!ThumbprintLocked) current.SigningThumbprint = _s.SigningThumbprint;
        current.Defaults.Display.Mode = _s.Defaults.Display.Mode;
        current.Defaults.Display.Multimon = _s.Defaults.Display.Multimon;
        current.Defaults.Redirect.Clipboard = _s.Defaults.Redirect.Clipboard;
        current.Defaults.Redirect.Printers = _s.Defaults.Redirect.Printers;
        current.Defaults.Redirect.Drives = _s.Defaults.Redirect.Drives;
        current.StatusRefreshSeconds = _s.StatusRefreshSeconds;
        current.ConfirmDisconnect = _s.ConfirmDisconnect;
        current.KeepRunningInTray = _s.KeepRunningInTray;
        var startChanged = _startWithWindows != await PackageInfo.IsStartWithWindowsEnabledAsync();
        current.StartWithWindows = _startWithWindows;
        try
        {
            Main.Controller.SaveSettings(current);
        }
        catch (ValidationException e)
        {
            Main.Flows?.Error("Settings could not be saved", e.Message);
            return;
        }
        if (startChanged && await PackageInfo.SetStartWithWindowsAsync(_startWithWindows) is { } problem) Main.Flows?.Error("Start with Windows", problem);
        Main.Flows?.SettingsSaved();
        Main.Toasts.Show("Settings saved");
        OnPropertyChanged(nameof(LaunchNote));
        OnPropertyChanged(nameof(LaunchLocked));
    }

    // ── Read-only information ────────────────────────────
    public AppPolicy Policy => Main.Policy!;
    public string PolicyDescription => Policy.Active
        ? "A policy from IT is active on this computer. Locked options are marked in the connection settings."
        : "No app policy is installed on this computer. Windows Group Policy for Remote Desktop still applies.";
    public string SavingPasswords => Policy.AllowSavedCredentials == false ? "Not allowed" : "Allowed";
    public string? LockedOptions => Policy.Redirect is { Any: true } r ? string.Join(", ", r.Entries().Select(e => $"{e.Key}: {e.Value}")) : null;

    public bool CentralConfigured => Main.Controller!.Central.Info.Configured;
    public string CentralSource => Policy.CentralList?.Url ?? Policy.CentralList?.Path ?? "";
    public string CentralStatus => Main.Controller!.Central.Info.State switch { "current" => "Up to date", "offline" => "Offline copy", "error" => "Not available", "loading" => "Loading", var s => s };
    public string? CentralVersion => Main.Controller!.Central.Info.Version is int v ? $"{v} · {Main.Controller.Central.Info.Count} systems" : null;
    public string? CentralLoaded => Main.Controller!.Central.Info.FetchedAt is { } t ? Format.DateTime(t) : null;
    public string? CentralError => Main.Controller!.Central.Info.Error;

    public string DataFolder => Main.Controller!.Paths.DataDir;
    public string Version => Main.Version;
    public string Packaging => PackageInfo.IsPackaged ? $"MSIX package {PackageInfo.PackageFullName}" : "Not packaged (development or portable build)";

    public MigrationRecord? Migration { get; private set; }
    public bool LegacyDataExists => Directory.Exists(Main.Controller!.Paths.LegacyDataDir);
    public string MigrationStatus => Migration switch
    {
        null => LegacyDataExists ? "Not imported yet." : "No data from the previous app version was found.",
        { Status: "completed" } m => $"Imported on {Format.When(m.CompletedAt)}: {m.Imported} new, {m.Merged} already present, {m.Rejected} not imported." + (m.SettingsImported ? " Settings were taken over." : ""),
        { } m => $"The import did not work: {m.Error}",
    };

    public void ReloadMigration()
    {
        Migration = JsonFile.TryRead(Main.Controller!.Paths.MigrationFile, RdpJsonContext.Default.MigrationRecord);
        OnPropertyChanged(nameof(Migration));
        OnPropertyChanged(nameof(MigrationStatus));
    }

    public bool AuthConfigured => Main.AuthConfigured;
    public string AuthStatus => Main.Auth.SignedIn ? $"Signed in as {Main.Auth.Username}" : Main.Auth.Error ?? "Not signed in";
}

public sealed class HelpPageViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Route => "help";
    public IReadOnlyList<ErrorCodeRow> Codes { get; } = DisconnectReasons.All().Select(c => new ErrorCodeRow(c.Code, c.Title, c.Message)).ToList();
    public IReadOnlyList<KeyValue> Shortcuts { get; } =
    [
        new("Ctrl + K", "Quick connect: find a system and connect"),
        new("Ctrl + F or /", "Search systems"),
        new("Ctrl + N", "Add a system"),
        new("Esc", "Close dialog, menu or tooltip"),
        new("Ctrl + Alt + Break", "Inside a session: switch between full screen and window"),
        new("Ctrl + Alt + Left arrow", "Inside a session tab: give the keyboard back to the app"),
        new("Ctrl + Alt + End", "Inside a session: security options (change password, sign out)"),
    ];
}

public sealed class AccessDeniedPageViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Route => "access-denied";
    public string Reason => Main.Controller?.Access.Reason ?? "You do not have access to this app.";
    public string SignedInAs => $"Signed in as {Main.User} on {Main.Computer}.";
    public string Request => $"If you need Remote Desktop access for your work, request it from {Main.Policy?.ServiceDeskName ?? "the IT Service Desk"}. Include your username and the systems you need.";
    public bool HasHelpUrl => Main.Policy?.HelpUrl is not null;
}

public sealed class SignInPageViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Route => "sign-in";
    public string Message => Main.Auth.SignedIn
        ? "Your account does not have a role that may use this app. Ask IT for access."
        : Main.Auth.Error ?? "Sign in with your work account to use the app.";
}

public sealed class ErrorPageViewModel(MainViewModel main, string message, bool corrupt) : PageViewModel(main)
{
    public override string Route => "error";
    public string Message { get; } = message;
    public bool Corrupt { get; } = corrupt;
    public string Title => Corrupt ? "Your system list could not be read" : "Something is blocking the app";
}
