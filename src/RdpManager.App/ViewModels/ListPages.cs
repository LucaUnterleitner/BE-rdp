using RdpManager.Core.Formatting;
using RdpManager.Core.Models;
using RdpManager.Core.Search;

namespace RdpManager.App.ViewModels;

/// <summary>Base of all pages shown in the Home tab or a details tab.</summary>
public abstract class PageViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public abstract string Route { get; }
    public virtual string? RouteId => null;
    /// <summary>Called when data the page shows changed.</summary>
    public virtual void Refresh() { }
}

/// <summary>A page that is a virtualized list of rows with a fixed header and optional toolbar.</summary>
public abstract class ListPageViewModel(MainViewModel main) : PageViewModel(main)
{
    public const double CardMinWidth = 280;
    public const double Gap = 24;
    private IReadOnlyList<Row> _rows = [];
    private int _columns = 3;
    private double _viewport;

    public abstract string Title { get; }
    public abstract string Description { get; }
    public virtual bool ShowAddImport => Main.Items.Count > 0;
    public virtual bool ShowToolbar => Main.Items.Count > 0;
    public virtual bool ShowViewToggle => true;
    public virtual bool ShowFavoritesFilter => true;

    public IReadOnlyList<Row> Rows { get => _rows; private set => Set(ref _rows, value); }
    public int Columns => _columns;

    /// <summary>Width of the list area (set by the view); cards per row follow from it.</summary>
    public double ViewportWidth
    {
        get => _viewport;
        set
        {
            _viewport = value;
            var cols = Math.Max(1, (int)((value - 64 + Gap) / (CardMinWidth + Gap)));
            if (cols == _columns) return;
            _columns = cols;
            if (Main.View == "grid") Refresh();
        }
    }

    public override void Refresh()
    {
        Rows = Build();
        OnPropertyChanged(nameof(ShowAddImport));
        OnPropertyChanged(nameof(ShowToolbar));
    }

    protected abstract List<Row> Build();

    // ── Shared blocks ────────────────────────────────────
    protected void AddSystems(List<Row> rows, IReadOnlyList<ConnectionItem> list)
    {
        if (Main.View == "list")
        {
            var showType = Protocols.Enabled.Count > 1;
            rows.Add(new ListHeaderRow(showType));
            for (var i = 0; i < list.Count; i++) rows.Add(new ListItemRow(list[i], showType, i == list.Count - 1));
            return;
        }
        for (var i = 0; i < list.Count; i += _columns)
            rows.Add(new CardRow(list.Skip(i).Take(_columns).ToList(), _columns));
    }

    protected void AddSkeleton(List<Row> rows, int cards = 6)
    {
        for (var i = 0; i < Math.Max(1, cards / _columns); i++) rows.Add(new SkeletonRow(_columns));
    }

    protected EmptyRow NoSystems() => new("server", "No systems yet",
        "Add the remote systems you work with, or import existing Remote Desktop (.rdp) files.",
        [
            new EmptyAction("Add system", Main.AddCommand, "primary", "plus"),
            new EmptyAction("Import connection files", Main.ImportCommand, "secondary", "upload"),
            new EmptyAction("Load sample systems (mock data)", Main.LoadSamplesCommand, "ghost"),
        ]);

    protected EmptyRow NoResults() => new("search", "No systems found", "Try changing the filters or search term.",
        [new EmptyAction("Clear filters", Main.ClearFiltersCommand)]);

    protected void AddHistory(List<Row> rows, IReadOnlyList<AuditEntry> events)
    {
        rows.Add(new HistoryHeaderRow());
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            rows.Add(new HistoryRow(e, e.ConnectionId is { } id ? Main.ItemById(id) : null, i == events.Count - 1, Main.ConnectCommand, Main.QuickHostCommand));
        }
    }

    protected static string Systems(int n) => n == 1 ? "1 system" : $"{n} systems";
}

public sealed class DashboardPageViewModel(MainViewModel main) : ListPageViewModel(main)
{
    public override string Route => "dashboard";
    public override string Title => "Dashboard";
    public override string Description => "Find a system, start a session and see what needs your attention.";

    protected override List<Row> Build()
    {
        var rows = new List<Row>();
        var all = Main.Items;
        if (!Main.IsLoaded) { AddSkeleton(rows); return rows; }
        rows.AddRange(Main.AttentionAlerts());
        if (all.Count == 0) { rows.Add(NoSystems()); return rows; }
        if (Main.Filters.IsActive)
        {
            var list = Main.Filtered(all);
            rows.Add(new ResultCountRow($"{list.Count} of {all.Count} systems"));
            if (list.Count > 0) AddSystems(rows, list); else rows.Add(NoResults());
            return rows;
        }
        var active = Main.ActiveSessionItems();
        var first = true;
        if (active.Count > 0)
        {
            rows.Add(new SectionHeaderRow("Active sessions", First: true));
            foreach (var s in active.Take(3)) rows.Add(new SessionCardRow(s));
            first = false;
        }
        var favs = all.Where(c => c.IsFavorite).ToList();
        if (favs.Count > 0)
        {
            rows.Add(new SectionHeaderRow("Favorites", Systems(favs.Count), First: first));
            AddSystems(rows, favs);
            first = false;
        }
        var available = all.Count(c => c.StatusKey == "available");
        rows.Add(new SectionHeaderRow("All systems", $"{available} of {all.Count} available", First: first));
        AddSystems(rows, all);
        var recent = Main.HistoryEvents().Take(5).ToList();
        rows.Add(new SectionHeaderRow("Recent activity", ActionText: recent.Count > 0 ? "View all" : null, ActionCommand: Main.NavigateCommand, ActionParameter: "recent"));
        if (recent.Count > 0) AddHistory(rows, recent);
        else rows.Add(new EmptyRow("clock", "No connections yet", "Your recent connections will appear here.", []));
        return rows;
    }

    /// <summary>The "x of y available" header changes with every status check; refresh only that row.</summary>
    public void RefreshAvailability()
    {
        if (Main.Filters.IsActive || !Main.IsLoaded) return;
        Refresh();
    }
}

public sealed class SystemsPageViewModel(MainViewModel main) : ListPageViewModel(main)
{
    public override string Route => "systems";
    public override string Title => "My systems";
    public override string Description => "All remote systems you can connect to, grouped by team or purpose.";
    public override bool ShowAddImport => true;

    protected override List<Row> Build()
    {
        var rows = new List<Row>();
        if (!Main.IsLoaded) { AddSkeleton(rows); return rows; }
        var all = Main.Items;
        if (all.Count == 0) { rows.Add(NoSystems()); return rows; }
        var list = Main.Filtered(all);
        if (list.Count == 0) { rows.Add(NoResults()); return rows; }
        rows.Add(new ResultCountRow($"{list.Count} of {all.Count} systems"));
        var groups = list.GroupBy(c => c.Folder.Length > 0 ? c.Folder : "Ungrouped")
            .OrderBy(g => g.Key == "Ungrouped").ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var first = true;
        foreach (var g in groups)
        {
            var items = g.ToList();
            rows.Add(new SectionHeaderRow(g.Key, items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), "folder", First: first));
            AddSystems(rows, items);
            first = false;
        }
        return rows;
    }
}

public sealed class FavoritesPageViewModel(MainViewModel main) : ListPageViewModel(main)
{
    public override string Route => "favorites";
    public override string Title => "Favorites";
    public override string Description => "Systems you use most often.";
    public override bool ShowAddImport => false;
    public override bool ShowToolbar => false;

    protected override List<Row> Build()
    {
        var rows = new List<Row>();
        if (!Main.IsLoaded) { AddSkeleton(rows, 3); return rows; }
        var favs = Main.Items.Where(c => c.IsFavorite).ToList();
        if (favs.Count > 0) AddSystems(rows, favs);
        else rows.Add(new EmptyRow("star", "No favorites yet", "Select the star on a system to keep it here for quick access.",
            [new EmptyAction("Go to My systems", Main.NavigateCommand, Parameter: "systems")]));
        return rows;
    }
}

public sealed class RecentPageViewModel(MainViewModel main) : ListPageViewModel(main)
{
    public override string Route => "recent";
    public override string Title => "Recent sessions";
    public override string Description => "Your connection history on this computer. Stored locally, without passwords.";
    public override bool ShowAddImport => false;
    public override bool ShowToolbar => false;

    protected override List<Row> Build()
    {
        var rows = new List<Row>();
        var events = Main.HistoryEvents().Take(100).ToList();
        if (events.Count > 0) AddHistory(rows, events);
        else rows.Add(new EmptyRow("clock", "No sessions yet", "Sessions you start appear here with their result and duration.", []));
        return rows;
    }
}

/// <summary>Filter helpers shared by the pages.</summary>
public static class FilterExtensions
{
    public static List<ConnectionItem> SortedByName(this IEnumerable<ConnectionItem> items)
        => items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static string FormatWhen(this AuditEntry e) => Format.When(e.Ts);
}
