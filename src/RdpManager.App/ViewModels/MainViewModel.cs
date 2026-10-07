using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RdpManager.App.Services;
using RdpManager.App.Sessions;
using RdpManager.Core.Formatting;
using RdpManager.Core.Models;
using RdpManager.Core.Search;
using RdpManager.Infrastructure.Auth;
using RdpManager.Infrastructure.Central;
using RdpManager.Infrastructure.Storage;

namespace RdpManager.App.ViewModels;

public sealed record PageTab(string Id, string Title, bool IsActive);

public sealed record Notification(string Kind, string Title, string Message, DateTimeOffset At)
{
    public string Icon => Kind switch { "error" => "xCircle", "success" => "checkCircle", _ => "info" };
    public string When => Format.When(At);
}

/// <summary>
/// The app shell: data, navigation, filters, tabs, banners and the commands of the main window
/// (port of app.js). All members are used on the UI thread; services report changes through events that are
/// marshalled here with BeginInvoke and coalesced, so a burst of updates causes one list rebuild.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, ConnectionItem> _byId = [];
    private List<ConnectionItem> _items = [];
    private List<SessionInfo> _sessions = [];
    private List<AuditEntry> _audit = [];
    private readonly Dictionary<string, SessionItem> _sessionItems = [];
    private readonly HashSet<string> _dismissed = [];
    private readonly DispatcherTimer _searchTimer;
    private bool _rebuildQueued;
    private PageViewModel _page;
    private string _query = "";
    private FilterState _filters = FilterState.Empty;
    private bool _isLoaded;
    private string? _loadError;
    private bool _sidebarCollapsed;
    private string _view = "grid";
    private bool _filtersOpen;
    private int _unread;
    private bool _signInRequired;
    private UpdateNotice? _update;

    /// <summary>The single shell instance (bound from row templates with x:Static instead of ancestor lookups).</summary>
    public static MainViewModel Instance { get; private set; } = null!;

    public MainViewModel()
    {
        Instance = this;
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => ApplySearch(), Dispatcher.CurrentDispatcher);
        _searchTimer.Stop();
        Dashboard = new DashboardPageViewModel(this);
        _page = Dashboard;
        NavigateCommand = new RelayCommand(p => Navigate(p as string ?? "dashboard"));
        AddCommand = new RelayCommand(() => Flows?.AddSystem());
        ImportCommand = new AsyncCommand(() => Flows?.ImportAsync() ?? Task.CompletedTask);
        LoadSamplesCommand = new RelayCommand(() => Flows?.LoadSamples());
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ConnectCommand = new AsyncCommand(p => Flows?.ConnectAsync(Resolve(p)) ?? Task.CompletedTask);
        ConnectOptionsCommand = new AsyncCommand(p => Flows?.ConnectWithOptionsAsync(Resolve(p)) ?? Task.CompletedTask);
        QuickHostCommand = new RelayCommand(p => Flows?.QuickConnect(p as string ?? (p as HistoryRow)?.Host ?? ""));
        QuickConnectCommand = new RelayCommand(() => Flows?.QuickConnect(""));
        FocusSessionCommand = new RelayCommand(p => Flows?.FocusSession(p as string ?? (p as ConnectionItem)?.ActiveSessionId ?? (p as SessionItem)?.Info.Id));
        DisconnectCommand = new AsyncCommand(p => Flows?.DisconnectAsync(p as string ?? (p as SessionItem)?.Info.Id) ?? Task.CompletedTask);
        ReconnectCommand = new AsyncCommand(p => Flows?.ReconnectAsync(p as string ?? (p as SessionItem)?.Info.ConnectionId) ?? Task.CompletedTask);
        FavoriteCommand = new RelayCommand(p => Flows?.ToggleFavorite(Resolve(p)));
        DetailsCommand = new RelayCommand(p => { if (Resolve(p) is { } c) OpenDetails(c.Id); });
        MenuCommand = new RelayCommand(p => Flows?.ShowSystemMenu(Resolve(p)));
        EditCommand = new RelayCommand(p => Flows?.Edit(Resolve(p)));
        DuplicateCommand = new RelayCommand(p => Flows?.Duplicate(Resolve(p)));
        DeleteCommand = new AsyncCommand(p => Flows?.DeleteAsync(Resolve(p)) ?? Task.CompletedTask);
        ExportCommand = new RelayCommand(p => Flows?.Export(Resolve(p)));
        ProbeAllCommand = new AsyncCommand(() => Controller?.RefreshStatusAsync() ?? Task.CompletedTask);
        RefreshCentralCommand = new AsyncCommand(() => Flows?.RefreshCentralAsync() ?? Task.CompletedTask);
        DismissCommand = new RelayCommand(p => { if (p is string k) { _dismissed.Add(k); RefreshBanners(); QueueRebuild(); } });
        ToggleSidebarCommand = new RelayCommand(() => { SidebarCollapsed = !SidebarCollapsed; Flows?.SaveViewPrefs(); });
        ToggleFiltersCommand = new RelayCommand(() => { FiltersOpen = !FiltersOpen; Flows?.SaveViewPrefs(); });
        SetViewCommand = new RelayCommand(p => { View = p as string == "list" ? "list" : "grid"; Flows?.SaveViewPrefs(); QueueRebuild(); });
        NotificationsCommand = new RelayCommand(() => { Unread = 0; Flows?.ShowNotifications(); });
        UserMenuCommand = new RelayCommand(p => Flows?.ShowUserMenu(p as UIElement));
        HomeTabCommand = new RelayCommand(() => ShowHome());
        PageTabCommand = new RelayCommand(p => { if (p is string id) OpenDetails(id); });
        ClosePageTabCommand = new RelayCommand(p => { if (p is string id) ClosePageTab(id); });
        SessionTabCommand = new RelayCommand(p => { if (p is string id && Tabs?.Main is { } h) Tabs.Activate(h, id); });
        CloseSessionTabCommand = new AsyncCommand(p => Flows?.DisconnectAsync(p as string) ?? Task.CompletedTask);
        StartEmptyCommand = new RelayCommand(() => Flows?.StartEmpty());
        RetryLoadCommand = new RelayCommand(() =>
        {
            LoadError = null;
            if (Controller is null) RetryStartup?.Invoke(); else Reload();
        });
        OpenDataFolderCommand = new RelayCommand(() => Flows?.OpenDataFolder());
        OpenHelpUrlCommand = new RelayCommand(() => Flows?.OpenHelpUrl());
        SignInCommand = new AsyncCommand(() => Flows?.SignInAsync() ?? Task.CompletedTask);
    }

    // ── Services (attached after startup) ─────────────────
    public AppController? Controller { get; private set; }
    public SessionTabManager? Tabs { get; private set; }
    public UiFlows? Flows { get; private set; }
    public ToastService Toasts { get; } = new();

    public void Attach(AppController controller, SessionTabManager tabs, UiFlows flows)
    {
        Controller = controller;
        Tabs = tabs;
        Flows = flows;
        OnPropertyChanged(nameof(MainHost));
        OnPropertyChanged(nameof(Policy));
        OnPropertyChanged(nameof(CanQuickConnect));
    }

    public TabHost? MainHost => Tabs?.Main;
    public AppPolicy? Policy => Controller?.Policy;

    // ── Identity ─────────────────────────────────────────
    public string User { get; } = AuditLog.CurrentUser;
    public string DisplayName { get; } = Environment.UserName;
    public string Initials => Format.Initials(DisplayName);
    public string Computer { get; } = Environment.MachineName;
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public AuthState Auth => Controller?.Auth.State ?? AuthState.SignedOut;
    public bool AuthConfigured => Controller?.Auth.Configured == true;

    public void RaiseAuthChanged()
    {
        OnPropertyChanged(nameof(Auth));
        OnPropertyChanged(nameof(AuthConfigured));
        SignInRequired = Controller is { Auth.RequireSignIn: true } c && (!c.Auth.State.SignedIn || !c.Auth.HasRequiredRole(c.Auth.State));
    }

    // ── State ────────────────────────────────────────────
    public bool IsLoaded { get => _isLoaded; private set { if (Set(ref _isLoaded, value)) OnPropertyChanged(nameof(CanQuickConnect)); } }
    public string? LoadError { get => _loadError; set { if (Set(ref _loadError, value)) UpdatePage(); } }
    public bool LoadCorrupt { get; private set; }
    /// <summary>Set when startup failed before services existed; "Try again" runs the load again.</summary>
    public Action? RetryStartup { get; set; }
    public bool AccessDenied => Controller is { Access.Allowed: false };
    public bool SignInRequired { get => _signInRequired; private set { if (Set(ref _signInRequired, value)) UpdatePage(); } }
    public bool CanQuickConnect => IsLoaded && !AccessDenied && !SignInRequired;

    public IReadOnlyList<ConnectionItem> Items => _items;
    public ConnectionItem? ItemById(string id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<SessionInfo> Sessions => _sessions;
    public IReadOnlyList<AuditEntry> Audit => _audit;

    public DashboardPageViewModel Dashboard { get; }
    public PageViewModel CurrentPage { get => _page; private set => Set(ref _page, value); }
    public string Route { get; private set; } = "dashboard";
    public (string Name, string? Id) HomeRoute { get; private set; } = ("dashboard", null);
    public ObservableCollection<PageTab> PageTabs { get; } = [];
    private readonly List<string> _pageTabIds = [];

    public bool SidebarCollapsed { get => _sidebarCollapsed; set => Set(ref _sidebarCollapsed, value); }
    public string View { get => _view; set { if (Set(ref _view, value)) { OnPropertyChanged(nameof(IsGrid)); OnPropertyChanged(nameof(IsList)); } } }
    public bool IsGrid { get => _view == "grid"; set { if (value) SetViewCommand.Execute("grid"); } }
    public bool IsList { get => _view == "list"; set { if (value) SetViewCommand.Execute("list"); } }
    public bool FiltersOpen { get => _filtersOpen; set { if (Set(ref _filtersOpen, value)) OnPropertyChanged(nameof(ShowCollapsedClear)); } }

    // ── Filters ──────────────────────────────────────────
    public FilterState Filters => _filters;

    /// <summary>Search text. Typing restarts a short timer, so the list is filtered once per pause, not per key.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    internal void ApplySearch()
    {
        _searchTimer.Stop();
        SetFilters(_filters with { Query = _query });
    }

    public string FilterProtocol { get => _filters.Protocol; set => SetFilters(_filters with { Protocol = value ?? "" }); }
    public string FilterStatus { get => _filters.Status; set => SetFilters(_filters with { Status = value ?? "" }); }
    public string FilterOs { get => _filters.Os; set => SetFilters(_filters with { Os = value ?? "" }); }
    public bool FilterFavorites { get => _filters.Favorites; set => SetFilters(_filters with { Favorites = value }); }
    public bool FilterRecent { get => _filters.Recent; set => SetFilters(_filters with { Recent = value }); }
    public int ActiveFilterCount => _filters.ActiveFilterCount;
    public bool HasActiveFilters => _filters.ActiveFilterCount > 0;
    public bool ShowCollapsedClear => !_filtersOpen && HasActiveFilters;
    public bool AnyFilter => _filters.IsActive;

    public IReadOnlyList<KeyValuePair<string, string>> StatusOptions { get; } =
    [
        new("", "All"), new("available", "Available"), new("busy", "Busy"), new("offline", "Offline"), new("unknown", "Unknown"),
    ];

    public IReadOnlyList<KeyValuePair<string, string>> OsOptions
        => [new("", "All"), .. _items.Select(i => i.Os).Where(o => o.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Select(o => new KeyValuePair<string, string>(o, o))];

    public IReadOnlyList<KeyValuePair<string, string>> ProtocolOptions
        => [new("", "All"), .. Protocols.All.Where(p => _items.Any(i => i.Protocol == p)).Select(p => new KeyValuePair<string, string>(p, Protocols.Label(p)))];

    public bool ShowProtocolFilter => Protocols.All.Count(p => _items.Any(i => i.Protocol == p)) > 1;
    public string SearchPlaceholder => Protocols.Enabled.Count > 1 ? "Search by name, host, type, OS, location or tag" : "Search by name, host, OS, location or tag";

    private void SetFilters(FilterState f)
    {
        if (f == _filters) return;
        _filters = f;
        OnPropertyChanged(nameof(FilterProtocol));
        OnPropertyChanged(nameof(FilterStatus));
        OnPropertyChanged(nameof(FilterOs));
        OnPropertyChanged(nameof(FilterFavorites));
        OnPropertyChanged(nameof(FilterRecent));
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ShowCollapsedClear));
        OnPropertyChanged(nameof(AnyFilter));
        RebuildNow();
    }

    public void ClearFilters()
    {
        _query = "";
        OnPropertyChanged(nameof(Query));
        SetFilters(FilterState.Empty);
    }

    /// <summary>Filtered, name-sorted items. Search terms are matched against prebuilt lower-case keys.</summary>
    public List<ConnectionItem> Filtered(IReadOnlyList<ConnectionItem> items)
    {
        var f = _filters;
        var terms = SystemFilter.Terms(f.Query);
        var now = DateTimeOffset.UtcNow;
        var result = new List<ConnectionItem>(items.Count);
        foreach (var c in items)
            if (SystemFilter.Matches(c.Model, c.SearchKey, terms, f, c.StatusKey, now)) result.Add(c);
        return result;
    }

    // ── Data ─────────────────────────────────────────────
    public void Reload()
    {
        if (Controller is null) return;
        var store = Controller.Store;
        if (store.Corrupt is { } corrupt)
        {
            LoadCorrupt = true;
            LoadError = $"Your system list file was damaged and could not be read ({corrupt.Error}). A copy was kept at {corrupt.Backup}.";
        }
        SetConnections(Controller.AllConnections());
        _sessions = Controller.Sessions.List().ToList();
        ApplySessions();
        IsLoaded = true;
        RefreshBanners();
        UpdatePage();
        _ = RefreshAuditAsync();
    }

    /// <summary>Updates items in place; only new or removed systems create or drop item objects.</summary>
    public void SetConnections(IReadOnlyList<Connection> list)
    {
        var status = Controller?.StatusSnapshot() ?? new Dictionary<string, ProbeResult>();
        var seen = new HashSet<string>();
        foreach (var c in list)
        {
            seen.Add(c.Id);
            if (_byId.TryGetValue(c.Id, out var item)) item.Update(c);
            else
            {
                item = new ConnectionItem(c) { Probe = status.GetValueOrDefault(c.Id) };
                item.StatusKeyChanged += OnStatusKeyChanged;
                _byId[c.Id] = item;
            }
        }
        foreach (var id in _byId.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _byId[id].StatusKeyChanged -= OnStatusKeyChanged;
            _byId.Remove(id);
        }
        _items = _byId.Values.SortedByName();
        ApplySessions();
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(OsOptions));
        OnPropertyChanged(nameof(ProtocolOptions));
        OnPropertyChanged(nameof(ShowProtocolFilter));
        // Close details tabs of removed systems.
        foreach (var id in _pageTabIds.Where(i => !_byId.ContainsKey(i)).ToList()) ClosePageTab(id);
        SyncPageTabs();
        QueueRebuild();
    }

    public void ApplyStatus(IReadOnlyDictionary<string, ProbeResult> results)
    {
        foreach (var (id, r) in results) if (_byId.TryGetValue(id, out var item)) item.Probe = r;
        RefreshBanners();
        if (CurrentPage is DashboardPageViewModel d) d.RefreshAvailability();
        if (CurrentPage is DetailsPageViewModel det) det.RaiseStatus();
    }

    private void OnStatusKeyChanged(ConnectionItem _)
    {
        if (_filters.Status.Length > 0) QueueRebuild();
    }

    public void OnSessionUpdate(SessionInfo s)
    {
        var prev = _sessions.FirstOrDefault(x => x.Id == s.Id);
        var i = _sessions.FindIndex(x => x.Id == s.Id);
        if (i < 0) _sessions.Insert(0, s); else _sessions[i] = s;
        _sessionItems.Remove(s.Id);
        ApplySessions();
        if (prev is not null && prev.State != s.State)
        {
            if (s.State == SessionState.Active) Notify("success", $"Connected to {s.Name}", s.Host);
            if (s.State == SessionState.Failed) Notify("error", $"Connection to {s.Name} failed", s.Result?.Title ?? "");
            if (s.State == SessionState.Ended) Notify("info", $"Session to {s.Name} ended", s.Result?.Title ?? "");
            if (s.State is SessionState.Ended or SessionState.Failed) _ = RefreshAuditAsync();
        }
        if (CurrentPage is DashboardPageViewModel) QueueRebuild();
        if (CurrentPage is DetailsPageViewModel det) det.RaiseStatus();
        OnPropertyChanged(nameof(Sessions));
    }

    private void ApplySessions()
    {
        var live = _sessions.Where(s => s.IsLive).GroupBy(s => s.ConnectionId).ToDictionary(g => g.Key, g => g.First());
        foreach (var item in _items)
        {
            var s = live.GetValueOrDefault(item.Id);
            if (item.Session?.Id != s?.Id || item.Session?.State != s?.State) item.Session = s;
        }
    }

    public List<SessionItem> ActiveSessionItems()
        => _sessions.Where(s => s.IsLive).Select(s =>
        {
            if (!_sessionItems.TryGetValue(s.Id, out var item)) _sessionItems[s.Id] = item = new SessionItem(s, FocusSessionCommand, DisconnectCommand, ReconnectCommand);
            return item;
        }).ToList();

    public void TickDurations()
    {
        foreach (var s in _sessionItems.Values) if (s.IsLive) s.Tick();
    }

    public async Task RefreshAuditAsync()
    {
        if (Controller is null) return;
        var store = Controller.Store;
        await store.Audit.FlushAsync();
        _audit = await Task.Run(() => store.Audit.ReadRecent(300));
        if (CurrentPage is ListPageViewModel or DetailsPageViewModel) QueueRebuild();
    }

    public IEnumerable<AuditEntry> HistoryEvents() => _audit.Where(e => e.Event is "session_ended" or "connect_blocked");

    // ── Rebuilds (coalesced) ─────────────────────────────
    public void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, RebuildNow);
    }

    public void RebuildNow()
    {
        _rebuildQueued = false;
        CurrentPage.Refresh();
    }

    // ── Banners and alerts ───────────────────────────────
    public sealed record Banner(string Kind, string Title, string Message, string? ActionText, ICommand? Action, string? DismissKey)
    {
        public string Icon => Kind switch { "error" => "xCircle", "warning" => "warning", _ => "info" };
    }

    private IReadOnlyList<Banner> _banners = [];
    public IReadOnlyList<Banner> Banners { get => _banners; private set => Set(ref _banners, value); }

    /// <summary>Offline/VPN hint and central list problems (shown above lists and details).</summary>
    public void RefreshBanners()
    {
        var list = new List<Banner>();
        if (IsLoaded && LooksOffline())
            list.Add(new Banner("warning", "You seem to be offline or not on the VPN",
                "None of your systems can be reached. Check your network connection and connect to the BearingPoint VPN, then check again.", "Check again", ProbeAllCommand, null));
        var central = Controller?.Central.Info ?? CentralInfo.Off;
        if (central.Configured && central.State == "error" && !_dismissed.Contains("central"))
            list.Add(new Banner("error", "The IT system list could not be loaded", $"{central.Error} Your personal systems are not affected.", "Try again", RefreshCentralCommand, "central"));
        else if (central.Configured && (central.State == "offline" || central.Stale) && !_dismissed.Contains("central"))
            list.Add(new Banner("info", $"IT system list from {Format.When(central.FetchedAt)}", "The current list could not be loaded, so the last verified copy is shown. Systems may have changed since then.", null, null, "central"));
        if (_update is { } u && !_dismissed.Contains("update"))
            list.Add(new Banner("info", u.Title, u.Message, null, null, "update"));
        Banners = list;
    }

    public sealed record UpdateNotice(string Title, string Message);

    public void ShowUpdate(UpdateNotice notice)
    {
        _update = notice;
        RefreshBanners();
    }

    /// <summary>Offline when every checked system fails with a network-level reason (usually: VPN is off).</summary>
    private bool LooksOffline()
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return true;
        var checkedItems = _items.Where(c => c.Probe is not null).ToList();
        if (checkedItems.Count < 2) return false;
        return checkedItems.All(c => !c.Probe!.Reachable && c.Probe.Reason is "dns" or "unreachable" or "timeout") && checkedItems.Any(c => !c.IsSample);
    }

    /// <summary>Policy notice and failed sessions (dashboard).</summary>
    public List<Row> AttentionAlerts()
    {
        var rows = new List<Row>();
        if (Policy?.Notice is { } n && !_dismissed.Contains("policy-notice"))
            rows.Add(new AlertRow("warning", n.Title, n.Message, [], "policy-notice", DismissCommand));
        foreach (var s in _sessions.Where(s => s.State == SessionState.Failed && !_dismissed.Contains(s.Id)).Take(2))
        {
            rows.Add(new AlertRow("error", $"Connection to {s.Name} failed", $"{s.Result?.Title}. {s.Result?.Message}",
                [new AlertAction("Try again", ReconnectCommand, s.ConnectionId)], s.Id, DismissCommand));
        }
        return rows;
    }

    // ── Notifications ────────────────────────────────────
    public ObservableCollection<Notification> Notifications { get; } = [];
    public int Unread { get => _unread; set { if (Set(ref _unread, value)) OnPropertyChanged(nameof(HasUnread)); } }
    public bool HasUnread => _unread > 0;

    public void Notify(string kind, string title, string message)
    {
        Notifications.Insert(0, new Notification(kind, title, message, DateTimeOffset.UtcNow));
        while (Notifications.Count > 30) Notifications.RemoveAt(Notifications.Count - 1);
        Unread++;
    }

    // ── Navigation ───────────────────────────────────────
    private readonly Dictionary<string, PageViewModel> _pages = [];

    public void Navigate(string name, string? id = null)
    {
        // Leaving a session tab: show the app again (the session keeps running in its tab).
        if (Tabs?.Main is { HasActiveSession: true } host) Tabs.Activate(host, null);
        if (name == "details" && id is not null && !_pageTabIds.Contains(id)) _pageTabIds.Add(id);
        if (name != "details") HomeRoute = (name, id);
        Route = name;
        RouteId = id;
        OnPropertyChanged(nameof(Route));
        if (name is "settings" or "recent" or "dashboard") _ = RefreshAuditAsync();
        UpdatePage();
    }

    public string? RouteId { get; private set; }

    private void UpdatePage()
    {
        PageViewModel page;
        if (Controller is null || !IsLoaded) page = LoadError is not null ? new ErrorPageViewModel(this, LoadError, false) : Dashboard;
        else if (AccessDenied && Route != "help") page = Get("access-denied", () => new AccessDeniedPageViewModel(this));
        else if (SignInRequired && Route != "help") page = Get("sign-in", () => new SignInPageViewModel(this));
        else if (LoadError is not null && Route is not ("settings" or "help")) page = new ErrorPageViewModel(this, LoadError, LoadCorrupt);
        else page = Route switch
        {
            "systems" => Get("systems", () => new SystemsPageViewModel(this)),
            "favorites" => Get("favorites", () => new FavoritesPageViewModel(this)),
            "recent" => Get("recent", () => new RecentPageViewModel(this)),
            "settings" => new SettingsPageViewModel(this),
            "help" => Get("help", () => new HelpPageViewModel(this)),
            "details" => RouteId is { } id && _byId.ContainsKey(id) ? new DetailsPageViewModel(this, id) : new ErrorPageViewModel(this, "System not found. It may have been removed.", false),
            _ => Dashboard,
        };
        if (page is ListPageViewModel lp && CurrentPage is ListPageViewModel old && lp != old) lp.ViewportWidth = old.ViewportWidth;
        CurrentPage = page;
        page.Refresh();
        SyncPageTabs();
        OnPropertyChanged(nameof(NavRoute));
    }

    private PageViewModel Get(string key, Func<PageViewModel> create)
    {
        if (!_pages.TryGetValue(key, out var p)) _pages[key] = p = create();
        return p;
    }

    /// <summary>Navigation item that is highlighted (details belong to the page they were opened from).</summary>
    public string NavRoute => Route == "details" ? HomeRoute.Name : Route;

    public void OpenDetails(string id) => Navigate("details", id);

    public void ShowHome()
    {
        if (Tabs?.Main is { HasActiveSession: true } host) Tabs.Activate(host, null);
        if (Route == "details") Navigate(HomeRoute.Name, HomeRoute.Id);
        SyncPageTabs();
    }

    public void ClosePageTab(string id)
    {
        var i = _pageTabIds.IndexOf(id);
        if (i < 0) return;
        _pageTabIds.RemoveAt(i);
        if (Route == "details" && RouteId == id)
        {
            if (_pageTabIds.Count > 0) Navigate("details", _pageTabIds[Math.Min(i, _pageTabIds.Count - 1)]);
            else Navigate(HomeRoute.Name, HomeRoute.Id);
        }
        SyncPageTabs();
    }

    public void SyncPageTabs()
    {
        var showing = Tabs?.Main is not { HasActiveSession: true };
        var wanted = _pageTabIds.Where(_byId.ContainsKey).Select(id => new PageTab(id, _byId[id].Name, showing && Route == "details" && RouteId == id)).ToList();
        if (wanted.SequenceEqual(PageTabs)) { OnPropertyChanged(nameof(HomeSelected)); return; }
        PageTabs.Clear();
        foreach (var t in wanted) PageTabs.Add(t);
        OnPropertyChanged(nameof(HomeSelected));
        OnPropertyChanged(nameof(ShowTabStrip));
    }

    public bool HomeSelected => Tabs?.Main is not { HasActiveSession: true } && Route != "details";

    /// <summary>The tab strip shows while tabs exist (pages, sessions, or sessions in other windows as drop target).</summary>
    public bool ShowTabStrip => MainHost is { } h && !h.Fullscreen && (h.Tabs.Count > 0 || h.Elsewhere || PageTabs.Count > 0);

    public void RaiseTabs()
    {
        OnPropertyChanged(nameof(ShowTabStrip));
        OnPropertyChanged(nameof(HomeSelected));
        SyncPageTabs();
    }

    private ConnectionItem? Resolve(object? p) => p switch
    {
        ConnectionItem c => c,
        string id => ItemById(id),
        ListItemRow r => r.Item,
        HistoryRow h => h.Connection,
        _ => null,
    };

    // ── Commands ─────────────────────────────────────────
    public ICommand NavigateCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand LoadSamplesCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectOptionsCommand { get; }
    public ICommand QuickHostCommand { get; }
    public ICommand QuickConnectCommand { get; }
    public ICommand FocusSessionCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand MenuCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ProbeAllCommand { get; }
    public ICommand RefreshCentralCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand ToggleFiltersCommand { get; }
    public ICommand SetViewCommand { get; }
    public ICommand NotificationsCommand { get; }
    public ICommand UserMenuCommand { get; }
    public ICommand HomeTabCommand { get; }
    public ICommand PageTabCommand { get; }
    public ICommand ClosePageTabCommand { get; }
    public ICommand SessionTabCommand { get; }
    public ICommand CloseSessionTabCommand { get; }
    public ICommand StartEmptyCommand { get; }
    public ICommand RetryLoadCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenHelpUrlCommand { get; }
    public ICommand SignInCommand { get; }

    public void ResetLoadError()
    {
        LoadCorrupt = false;
        LoadError = null;
    }
}
