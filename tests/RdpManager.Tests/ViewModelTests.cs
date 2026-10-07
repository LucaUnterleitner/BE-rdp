using System.Text.RegularExpressions;
using System.Windows.Media;
using RdpManager.App.ViewModels;
using RdpManager.Core.Formatting;
using RdpManager.Core.Models;
using RdpManager.Core.Search;

namespace RdpManager.Tests;

/// <summary>View models and UI logic that do not need a window.</summary>
public class ViewModelTests
{
    private static ConnectionItem Item(string name, string host, string os = "", bool fav = false, string? last = null, List<string>? tags = null)
        => new(CoreTests.Norm(new Connection { Name = name, Host = host, Os = os, Favorite = fav, LastConnectedAt = last, Tags = tags ?? [] }));

    [Fact]
    public void Search_matches_every_term_against_name_host_os_and_tags()
    {
        var items = new[] { Item("Finance Prod", "fin-prod.corp", "Windows Server 2022", tags: ["sap"]), Item("Build Agent", "build01.corp") };
        bool Match(ConnectionItem c, string q) => SystemFilter.Matches(c.Model, c.SearchKey, SystemFilter.Terms(q), FilterState.Empty with { Query = q }, c.StatusKey, DateTimeOffset.UtcNow);
        Assert.True(Match(items[0], "fin sap"));
        Assert.True(Match(items[0], "SERVER 2022"));
        Assert.False(Match(items[0], "fin build"));
        Assert.True(Match(items[1], "build01"));
    }

    [Fact]
    public void Filters_by_status_os_favorites_and_recent_use()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = Item("A", "a", "Win", true, now.AddDays(-1).ToString("o"));
        var old = Item("B", "b", "Linux", false, now.AddDays(-30).ToString("o"));
        recent.Probe = ProbeResult.Ok(5);
        old.Probe = ProbeResult.Fail("dns");
        bool M(ConnectionItem c, FilterState f) => SystemFilter.Matches(c.Model, c.SearchKey, [], f, c.StatusKey, now);
        Assert.True(M(recent, new FilterState(Status: "available")));
        Assert.False(M(old, new FilterState(Status: "available")));
        Assert.True(M(old, new FilterState(Status: "offline")));
        Assert.True(M(recent, new FilterState(Favorites: true)));
        Assert.False(M(old, new FilterState(Recent: true)));
        Assert.False(M(recent, new FilterState(Os: "Linux")));
        Assert.Equal(2, new FilterState(Status: "x", Favorites: true).ActiveFilterCount);
        Assert.Equal("Name not found", recent.StatusLabel == "Available" ? SystemFilter.ReasonText("rdp", "dns") : "");
    }

    [Fact]
    public void Connection_item_reports_status_and_session_changes()
    {
        var item = Item("A", "a.corp", "Windows 11 Enterprise");
        Assert.Equal("checking", item.StatusKey);
        Assert.Equal("monitor", item.CardIcon);
        var changed = 0;
        item.StatusKeyChanged += _ => changed++;
        item.Probe = ProbeResult.Ok(12);
        Assert.Equal(("available", "12 ms"), (item.StatusKey, item.LatencyText));
        item.Session = new SessionInfo { Id = "s", ConnectionId = item.Id, Name = "A", Host = "a.corp", State = SessionState.Connecting };
        Assert.True(item.IsConnecting);
        Assert.Equal("busy", item.StatusKey);
        item.Session = null;
        Assert.True(item.IsIdle);
        Assert.Equal(3, changed);
    }

    [Fact]
    public void Quick_connect_offers_the_typed_address_and_matching_systems()
    {
        var items = new[] { Item("Finance Prod", "fin-prod.corp"), Item("Other", "other.corp") };
        var vm = new QuickConnectViewModel(items, _ => true, "fin");
        Assert.Equal("Finance Prod", vm.Options.Last().Title);
        Assert.True(vm.Options[0].IsAddress);
        vm.Text = "10.20.30.40:3390";
        Assert.Equal("Connect to 10.20.30.40:3390", vm.Options[0].Title);
        vm.Text = "a;calc";
        Assert.True(vm.IsEmpty);
        Assert.Contains("Not a valid address", vm.EmptyText);
        vm.Text = "fin-prod.corp";
        Assert.DoesNotContain(vm.Options, o => o.IsAddress); // already saved
    }

    [Fact]
    public void Edit_system_builds_a_valid_payload_and_reports_problems()
    {
        var vm = new EditSystemViewModel(null, new AppSettings(), null, [], null);
        Assert.Null(vm.BuildPayload(out var general));
        Assert.True(general);
        Assert.True(vm.HasHostError);
        vm.Host = "srv01.corp";
        vm.Tags = "a, b";
        vm.Options.GatewayMode = "always";
        Assert.Null(vm.BuildPayload(out _));
        Assert.True(vm.Options.HasGatewayError);
        vm.Options.GatewayHost = "gw.corp";
        var c = vm.BuildPayload(out _)!;
        Assert.Equal(("srv01.corp", 3389, "gw.corp"), (c.Host, c.Port, c.Gateway!.Host));
        Assert.Equal(["a", "b"], c.Tags);
        Assert.Equal("Starts: Remote Desktop to srv01.corp", vm.Preview);
        Assert.Equal("", c.Id);
    }

    [Fact]
    public void Edit_keeps_id_and_shows_saved_password_owner()
    {
        var existing = CoreTests.Norm(new Connection { Name = "Srv", Host = "srv", Port = 3390 });
        var vm = new EditSystemViewModel(existing, new AppSettings(), new AppPolicy { AllowSavedCredentials = false }, ["Finance", "Finance"], "CORP\\bob");
        Assert.Equal("Edit Srv", vm.Title);
        Assert.Equal("3390", vm.Port);
        Assert.False(vm.AllowSavedPassword);
        Assert.Single(vm.Folders);
        Assert.Equal(existing.Id, vm.BuildPayload(out _)!.Id);
    }

    [Fact]
    public void Options_respect_it_policy_locks()
    {
        var c = CoreTests.Norm(new Connection { Host = "x" });
        var o = new OptionsViewModel(c, new AppPolicy { Redirect = new PolicyRedirect { Clipboard = false, Drives = "all" } }, tabsMode: true);
        Assert.False(o.Clipboard);
        Assert.True(o.ClipboardLocked);
        Assert.True(o.Drives);
        Assert.False(o.PrintersLocked);
        Assert.False(o.ShowWindowOptions);
    }

    [Fact]
    public void Formatting_helpers()
    {
        Assert.Equal("1 h 05 min", Format.Duration(3900));
        Assert.Equal("3 min", Format.Duration(200));
        Assert.Equal("45 s", Format.Duration(45));
        Assert.Equal("JD", Format.Initials("CORP\\jane.doe"));
        Assert.Equal("?", Format.Initials(""));
        Assert.StartsWith("Today", Format.When(DateTimeOffset.Now));
        Assert.StartsWith("Yesterday", Format.When(DateTimeOffset.Now.AddDays(-1)));
    }

    [Fact]
    public void Every_icon_geometry_parses()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "RdpManager.slnx"))) root = root.Parent!;
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "src", "RdpManager.App", "Themes", "Icons.xaml"));
        var matches = Regex.Matches(xaml, "<Geometry x:Key=\"([^\"]+)\"[^>]*>([^<]+)</Geometry>");
        Assert.True(matches.Count >= 40);
        foreach (Match m in matches)
        {
            var g = Geometry.Parse(m.Groups[2].Value);
            Assert.False(g.Bounds.IsEmpty, m.Groups[1].Value);
        }
    }
}
