namespace RdpManager.Core.Models;

/// <summary>Connection types. Mirrors targets.js of the Electron app.</summary>
public static class Protocols
{
    public const string Rdp = "rdp";
    public const string Ssh = "ssh";
    public const string Web = "web";

    public static readonly IReadOnlyList<string> All = [Rdp, Ssh, Web];

    /// <summary>
    /// Connection types offered in the app. SSH and web are fully implemented but switched off, as in the
    /// Electron app (ENABLED_PROTOCOLS). Add Ssh and/or Web here to bring them back.
    /// </summary>
    public static readonly IReadOnlyList<string> Enabled = [Rdp];

    public static string Label(string? protocol) => protocol switch
    {
        Ssh => "SSH",
        Web => "Web",
        _ => "RDP",
    };

    /// <summary>Older records without a type are RDP.</summary>
    public static string Of(Connection? c) => string.IsNullOrEmpty(c?.Protocol) ? Rdp : c!.Protocol;

    public static int DefaultPort(string protocol, string scheme = "https") => protocol switch
    {
        Ssh => 22,
        Web => scheme == "http" ? 80 : 443,
        _ => 3389,
    };
}
