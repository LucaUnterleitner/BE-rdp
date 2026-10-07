using System.Windows.Input;
using RdpManager.Core.Formatting;
using RdpManager.Core.Models;

namespace RdpManager.App.ViewModels;

/// <summary>
/// Pages with lists are one virtualized list of heterogeneous rows (headers, card rows, list rows, history rows).
/// Only the rows on screen get WPF elements, so thousands of systems scroll smoothly.
/// </summary>
public abstract record Row;

public sealed record SectionHeaderRow(string Title, string? Meta = null, string? Icon = null, string? ActionText = null, ICommand? ActionCommand = null, object? ActionParameter = null, bool First = false) : Row;

public sealed record ResultCountRow(string Text) : Row;

public sealed record CardRow(IReadOnlyList<ConnectionItem> Items, int Columns) : Row;

public sealed record ListHeaderRow(bool ShowType) : Row;

public sealed record ListItemRow(ConnectionItem Item, bool ShowType, bool Last) : Row;

public sealed record EmptyAction(string Label, ICommand Command, string Style = "secondary", string? Icon = null, object? Parameter = null);

public sealed record EmptyRow(string Icon, string Title, string Message, IReadOnlyList<EmptyAction> Actions) : Row;

public sealed record SkeletonRow(int Columns) : Row;

public sealed record AlertAction(string Label, ICommand Command, object? Parameter = null);

/// <summary>Inline alert (kind: info, warning, error, success).</summary>
public sealed record AlertRow(string Kind, string Title, string Message, IReadOnlyList<AlertAction> Actions, string? DismissKey = null, ICommand? DismissCommand = null) : Row
{
    public string Icon => Kind switch { "error" => "xCircle", "warning" => "warning", "success" => "checkCircle", _ => "info" };
}

public sealed record SessionCardRow(SessionItem Session) : Row;

public sealed record HistoryHeaderRow : Row;

public sealed record HistoryRow(AuditEntry Entry, ConnectionItem? Connection, bool Last, ICommand ConnectCommand, ICommand QuickHostCommand) : Row
{
    public string Name => Entry.Name ?? Entry.Host ?? "";
    public string Host => Entry.Host ?? "";
    public string When => Format.When(Entry.Ts);
    public string Duration => Entry.DurationSec is > 0 ? Format.Duration(Entry.DurationSec.Value) : "–";
    public bool Blocked => Entry.Event == "connect_blocked";
    public bool Ok => Entry.Event == "session_ended" && Entry.State == "ended";
    public string ResultText => Blocked ? "Not reachable" : Ok ? Entry.Title ?? "Ended" : Entry.Title ?? "Failed";
    public string? CodeText => !Ok && !Blocked && Entry.Code is { Length: > 0 } c ? $"({c})" : null;
}

/// <summary>A session as shown on the dashboard.</summary>
public sealed class SessionItem(SessionInfo s, ICommand focus, ICommand disconnect, ICommand reconnect) : ObservableObject
{
    public SessionInfo Info { get; } = s;
    public ICommand FocusCommand { get; } = focus;
    public ICommand DisconnectCommand { get; } = disconnect;
    public ICommand ReconnectCommand { get; } = reconnect;
    public string Name => Info.Name;
    public string HostLine => Info.Gateway.Length > 0 ? $"{Info.Host} via {Info.Gateway}" : Info.Host;
    public bool IsLive => Info.IsLive;
    public bool IsSsh => Info.Protocol == Protocols.Ssh;
    public string StateText => Info.State switch
    {
        SessionState.Connecting => "Connecting",
        SessionState.Active => "Active",
        SessionState.Reconnecting => "Reconnecting",
        SessionState.Ended => "Ended",
        _ => "Failed",
    };
    public string StateKey => Info.State.ToString().ToLowerInvariant();
    public string Started => Format.When(Info.StartedAt);
    public string Duration
    {
        get
        {
            var since = Info.ConnectedAt ?? Info.StartedAt;
            var secs = Info.IsLive ? (DateTimeOffset.UtcNow - since).TotalSeconds : Info.ConnectedAt is { } c && Info.EndedAt is { } e ? (e - c).TotalSeconds : 0;
            return secs > 0 ? Format.Duration(secs) : "–";
        }
    }
    public string LaunchText => Info.LaunchMode switch { "direct" => "Direct", "embedded" => "Tab in the app", _ => Info.Signed ? "Signed file" : "Connection file" };
    public string DisconnectLabel => IsSsh ? "Close terminal" : "Disconnect";
    public void Tick() => OnPropertyChanged(nameof(Duration));
}
