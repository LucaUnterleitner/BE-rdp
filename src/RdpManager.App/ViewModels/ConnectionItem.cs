using System.Text.RegularExpressions;
using RdpManager.Core.Formatting;
using RdpManager.Core.Models;
using RdpManager.Core.Search;

namespace RdpManager.App.ViewModels;

/// <summary>
/// One system as shown in cards, rows and details. Status and session changes raise property notifications on
/// this object only, so lists are not rebuilt when a reachability check finishes.
/// </summary>
public sealed partial class ConnectionItem : ObservableObject
{
    private Connection _model;
    private ProbeResult? _probe;
    private SessionInfo? _session;

    public ConnectionItem(Connection model)
    {
        _model = model;
        SearchKey = SystemFilter.BuildSearchKey(model);
    }

    public Connection Model => _model;
    public string Id => _model.Id;
    public string SearchKey { get; private set; }

    /// <summary>Replaces the model (after an edit). Returns true when list-relevant fields changed.</summary>
    public bool Update(Connection model)
    {
        var listChanged = model.Name != _model.Name || model.Folder != _model.Folder || model.Os != _model.Os
            || model.Favorite != _model.Favorite || model.LastConnectedAt != _model.LastConnectedAt
            || model.Host != _model.Host || !model.Tags.SequenceEqual(_model.Tags) || model.Location != _model.Location || model.Protocol != _model.Protocol;
        _model = model;
        SearchKey = SystemFilter.BuildSearchKey(model);
        OnAllPropertiesChanged();
        return listChanged;
    }

    public ProbeResult? Probe
    {
        get => _probe;
        set
        {
            if (_probe == value) return;
            var oldKey = StatusKey;
            _probe = value;
            RaiseStatus(oldKey);
        }
    }

    public SessionInfo? Session
    {
        get => _session;
        set
        {
            var oldKey = StatusKey;
            _session = value is { IsLive: true } ? value : null;
            RaiseStatus(oldKey);
            OnPropertyChanged(nameof(Session));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ActiveSessionId));
        }
    }

    /// <summary>Raised when the status key changed (filters by status must re-run).</summary>
    public event Action<ConnectionItem>? StatusKeyChanged;

    private void RaiseStatus(string oldKey)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusKey));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusTooltip));
        OnPropertyChanged(nameof(StatusDot));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(LatencyText));
        if (oldKey != StatusKey) StatusKeyChanged?.Invoke(this);
    }

    public string Protocol => Protocols.Of(_model);
    public string Name => _model.Name;
    public string Host => _model.Host;
    public string Os => _model.Os;
    public string Location => _model.Location;
    public string Folder => _model.Folder;
    public bool IsFavorite => _model.Favorite;
    public bool IsSample => _model.Sample;
    public bool IsCentral => _model.IsCentral;
    public bool IsRdp => Protocol == Protocols.Rdp;

    /// <summary>Address as shown on cards: the port only when it is not the default for the type.</summary>
    public string Address
    {
        get
        {
            var scheme = _model.Web?.Scheme ?? "https";
            var port = _model.Port != 0 && _model.Port != Protocols.DefaultPort(Protocol, scheme) ? $":{_model.Port}" : "";
            if (Protocol == Protocols.Web) return $"{(scheme == "http" ? "http" : "https")}://{_model.Host}{port}{_model.Web?.Path}";
            return $"{(Protocol == Protocols.Ssh && _model.Username.Length > 0 ? _model.Username + "@" : "")}{_model.Host}{port}";
        }
    }

    public string MetaLine => _model.Os.Length > 0 ? (_model.Location.Length > 0 ? $"{_model.Os} · {_model.Location}" : _model.Os) : _model.Location;

    public string LastUsedText => _model.LastConnectedAt is null ? "Not used yet" : $"Last used: {Format.When(_model.LastConnectedAt)}";

    public DateTimeOffset? LastUsed => Format.ParseIso(_model.LastConnectedAt);

    [GeneratedRegex(@"windows 1\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkstationRe();

    /// <summary>Icon key: monitor for Windows 10/11 workstations, server otherwise (RDP).</summary>
    public string CardIcon => Protocol switch
    {
        Protocols.Ssh => "terminal",
        Protocols.Web => "globe",
        _ => WorkstationRe().IsMatch(_model.Os) ? "monitor" : "server",
    };

    public string ConnectLabel => Protocol == Protocols.Web ? "Open" : "Connect";
    public string ProtocolLabel => Protocols.Label(Protocol);

    public SystemStatus Status => SystemFilter.StatusOf(Protocol, _session?.State, _probe);
    public string StatusKey => Status.Key;
    public string StatusLabel => Status.Label;
    public bool StatusDot => Status.Dot;
    public string StatusIcon => Status.Icon;
    public string? StatusTooltip => Status.Reason is null ? null : SystemFilter.ReasonText(Protocol, Status.Reason) is { Length: > 0 } t ? t : null;
    public string? LatencyText => Status.LatencyMs is int ms ? $"{ms} ms" : null;

    public bool IsConnected => _session is not null;
    public bool IsConnecting => _session?.State == SessionState.Connecting;
    public bool IsIdle => _session is null;
    public string? ActiveSessionId => _session?.Id;
}
