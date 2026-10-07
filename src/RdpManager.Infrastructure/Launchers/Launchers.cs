using System.Diagnostics;
using RdpManager.Core.Models;
using RdpManager.Core.Targets;

namespace RdpManager.Infrastructure.Launchers;

/// <summary>
/// Launch details for SSH and web connections (port of launchers.js). Values arrive validated by
/// ConnectionNormalizer; every value is checked again before it is put on a command line (defense in depth).
/// SSH and web are switched off in this version (Protocols.Enabled).
/// </summary>
public static class Launchers
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    public static readonly string SshExe = Path.Combine(System32, "OpenSSH", "ssh.exe");
    public static readonly string CmdExe = Path.Combine(System32, "cmd.exe");

    // Characters that cmd.exe or ssh would treat specially. None of them can pass the field validation.
    private static readonly char[] Unsafe = ['"', '%', '&', '|', '<', '>', '^', '!', '(', ')'];

    public static bool SshAvailable => File.Exists(SshExe);

    /// <summary>ssh.exe arguments: options first, then "--" so the host can never be read as an option.</summary>
    public static List<string> SshArgs(Connection conn, string? strictHostKeyChecking)
    {
        if (!HostRules.IsHost(conn.Host)) throw new InvalidOperationException("The computer name is not valid.");
        if (conn.Username.Length > 0 && !HostRules.IsSshUser(conn.Username)) throw new InvalidOperationException("The username is not valid for SSH.");
        var args = new List<string>();
        if (conn.Username.Length > 0) { args.Add("-l"); args.Add(conn.Username); }
        args.Add("-p"); args.Add((conn.Port == 0 ? 22 : conn.Port).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var o = conn.Ssh ?? new SshOptions();
        if (o.IdentityFile.Length > 0)
        {
            if (!File.Exists(o.IdentityFile)) throw new InvalidOperationException($"The key file {o.IdentityFile} was not found.");
            args.AddRange(["-i", o.IdentityFile, "-o", "IdentitiesOnly=yes"]);
        }
        if (o.JumpHost.Length > 0) { args.Add("-J"); args.Add(o.JumpHost); }
        if (strictHostKeyChecking is "accept-new" or "yes") { args.Add("-o"); args.Add($"StrictHostKeyChecking={strictHostKeyChecking}"); }
        args.Add("--");
        args.Add(conn.Host);
        foreach (var a in args)
            if (a.IndexOfAny(Unsafe) >= 0 || a.Any(char.IsControl)) throw new InvalidOperationException("A connection value contains characters that are not allowed.");
        return args;
    }

    /// <summary>
    /// cmd.exe arguments. ssh runs inside cmd.exe only so that the window stays open after a failed connection
    /// (exit code 255). cmd runs without AutoRun scripts (/d) and without delayed expansion (/v:off).
    /// </summary>
    public static string SshCmdArguments(Connection conn, string? strictHostKeyChecking)
    {
        var args = string.Join(' ', SshArgs(conn, strictHostKeyChecking).Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
        const string onError = "if errorlevel 255 (echo. & echo The SSH connection ended with an error. Read the message above. & pause & exit /b 255)";
        return $"/d /v:off /s /c \"\"{SshExe}\" {args} & {onError}\"";
    }

    /// <summary>The URL for a web connection, limited to http and https without credentials.</summary>
    public static string WebTarget(Connection conn)
    {
        var url = new Uri(HostRules.WebUrl(conn.Host, conn.Port, conn.Web));
        if (url.Scheme is not ("http" or "https")) throw new InvalidOperationException("Only http and https addresses can be opened.");
        if (url.UserInfo.Length > 0) throw new InvalidOperationException("The web address must not contain a username or password.");
        return url.AbsoluteUri;
    }

    public static void OpenUrl(string url)
    {
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
