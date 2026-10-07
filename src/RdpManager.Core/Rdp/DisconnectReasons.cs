using RdpManager.Core.Models;

namespace RdpManager.Core.Rdp;

/// <summary>
/// User-facing explanations for RDP disconnect reasons and pre-connect probe results (port of errors.js).
/// Each entry says what happened, why it matters and what to do next.
/// </summary>
public static class DisconnectReasons
{
    private sealed record Entry(string Kind, string Title, string Message, bool Retry = false);

    private static Entry Dns() => new("error", "Computer name not found", "The name could not be resolved. Check the spelling, use the full name (for example host.domain.local) or connect to the VPN.", true);
    private static Entry Timeout() => new("error", "The connection timed out", "The network may be slow or a firewall is dropping traffic. Try again, or connect via the RD Gateway.", true);
    private static Entry Secure() => new("error", "A secure connection could not be established", "Encryption or certificate negotiation failed. Both computers may need current Windows updates. Contact the system owner.");
    private static Entry PwdExpired() => new("error", "Password expired", "Your password must be changed before you can sign in remotely. Change it on your BearingPoint laptop (Ctrl+Alt+Del, Change a password), then try again.");
    private static Entry Delegation() => new("error", "Saved credentials not allowed", "Policy does not allow saved or default credentials for this server. Use the full computer name so Kerberos is used, or type your credentials when asked.", true);
    private static Entry Lic() => new("error", "No Remote Desktop licenses available", "The server has no free Remote Desktop licenses. Contact IT.");

    private static readonly Dictionary<int, Entry> Reasons = new()
    {
        [0x1] = new("normal", "Session closed", "You closed the connection. The remote session may still be running on the server."),
        [0x2] = new("normal", "Disconnected remotely", "The session was disconnected by the remote side or by another sign-in. Reconnect to continue where you left off.", true),
        [0x3] = new("error", "The remote computer ended the session", "An administrator signed you out, a time limit was reached, or the server restarted. Reconnect. If this keeps happening, contact the system owner.", true),
        [0x104] = Dns(), [0x208] = Dns(), [0x508] = Dns(), [0x604] = Dns(),
        [0x204] = new("error", "The remote computer could not be reached", "Check that the system is switched on and that you are connected to the BearingPoint network or VPN. Remote Desktop may be disabled or a firewall may block port 3389.", true),
        [0x108] = Timeout(), [0x704] = Timeout(),
        [0x904] = new("error", "The connection was closed unexpectedly", "The network connection dropped, for example because the VPN reconnected. Reconnecting is safe. If it happens repeatedly, try the RD Gateway or contact IT.", true),
        [0x406] = Secure(), [0x606] = Secure(), [0x706] = Secure(), [0x906] = Secure(), [0xA06] = Secure(), [0xB06] = Secure(), [0xC06] = Secure(), [0xC08] = Secure(),
        [0x507] = new("error", "Sign-in failed", "Retry with your username and password, or check that your smart card is inserted.", true),
        [0x807] = new("error", "Wrong username or password", "Check your username (DOMAIN\\username) and password. If you saved a password, it may be outdated. Remove it in the system details and try again.", true),
        [0xA07] = new("error", "Account not found", "The user account does not exist on the remote system or domain. Check the username."),
        [0xB07] = new("error", "Account disabled", "Your account is disabled. Contact the IT Service Desk."),
        [0xD07] = new("error", "Account locked", "Your account is locked after too many failed sign-ins. Wait or contact the IT Service Desk. Retrying now may extend the lock."),
        [0xE07] = new("error", "Account expired", "Your account has expired. Contact the IT Service Desk."),
        [0xF07] = PwdExpired(), [0x1207] = PwdExpired(),
        [0x1807] = new("error", "Domain controller not reachable", "Your sign-in could not be verified. Connect to the VPN and check the domain part of your username.", true),
        [0x1607] = Delegation(), [0x1707] = Delegation(),
        [0x2107] = new("error", "Fresh credentials required", "The server does not accept saved passwords. Type your password when Windows asks for it.", true),
        [0x1B07] = new("error", "Server certificate expired", "The identity of the remote system cannot be confirmed. Do not continue. Report this to the system owner."),
        [0x1C07] = new("error", "Wrong smart card PIN", "Check your PIN and try again. Several wrong attempts block the card.", true),
        [0x2207] = new("error", "Smart card blocked", "Your smart card is blocked. Contact the IT Service Desk."),
        [0x808] = Lic(), [0x908] = Lic(),
        [0x112F] = new("error", "Protocol error", "The server may be low on memory or graphics resources. Try a lower resolution or a single monitor. If it persists, ask the system owner to check the server.", true),
    };

    /// <summary>
    /// Disconnect reasons during connection setup that mstsc cannot recover from (name resolution, network,
    /// TLS/certificate). For others, such as a wrong password, mstsc lets the user retry.
    /// </summary>
    public static readonly IReadOnlySet<int> FatalCodes = new HashSet<int> { 0x104, 0x208, 0x508, 0x604, 0x204, 0x108, 0x704, 0x406, 0x606, 0x706, 0x906, 0xA06, 0xB06, 0xC06, 0xC08, 0x1B07 };

    public static string Hex(int code) => "0x" + code.ToString("X");

    public static SessionResult? Explain(int? code)
    {
        if (code is null) return null;
        if (Reasons.TryGetValue(code.Value, out var e)) return new SessionResult(e.Kind, e.Title, e.Message, Hex(code.Value), e.Retry);
        return new SessionResult("error", "Connection could not be established",
            "The Remote Desktop client reported an error that this app does not know yet. Try again. If it repeats, send the technical code to IT.", Hex(code.Value), true);
    }

    public static bool IsKnown(int? code) => code is not null && Reasons.ContainsKey(code.Value);

    /// <summary>All error codes with their explanation, for the help page.</summary>
    public static IReadOnlyList<(string Code, string Title, string Message)> All()
        => Reasons.Where(kv => kv.Value.Kind == "error").Select(kv => (Hex(kv.Key), kv.Value.Title, kv.Value.Message)).ToList();

    private static readonly Dictionary<string, (string Title, string Message)> ProbeResults = new()
    {
        ["dns"] = ("Computer name not found", "The name could not be resolved. Check the spelling or connect to the VPN."),
        ["refused"] = ("Remote Desktop is not accepting connections", "The computer answered, but Remote Desktop is disabled or uses a different port."),
        ["timeout"] = ("The remote computer could not be reached", "No answer within 3 seconds. Check that the system is online and that the VPN is active. A firewall may block port 3389."),
        ["unreachable"] = ("Network unreachable", "Your computer has no route to this system. Check your network or VPN connection."),
        ["error"] = ("Availability could not be checked", "The check failed for an unexpected reason."),
    };

    public static (string Title, string Message) ExplainProbe(string? reason)
        => reason is not null && ProbeResults.TryGetValue(reason, out var v) ? v : ProbeResults["error"];
}
