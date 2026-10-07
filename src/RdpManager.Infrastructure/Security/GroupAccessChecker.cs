using System.Security.Principal;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Security;

public sealed record AccessResult(bool Allowed, string? Reason = null)
{
    public static readonly AccessResult Granted = new(true);
}

/// <summary>
/// Members of an allowed AD group may use the app (policy "allowedGroups"). Uses the groups of the Windows
/// logon token (the same list "whoami /groups" prints), matched by full name (DOMAIN\Group) or short name.
/// </summary>
public static class GroupAccessChecker
{
    public static AccessResult Check(IReadOnlyList<string>? allowedGroups)
    {
        if (allowedGroups is null || allowedGroups.Count == 0) return AccessResult.Granted;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var names = new List<string>();
            foreach (var sid in identity.Groups ?? new IdentityReferenceCollection())
            {
                try { names.Add(sid.Translate(typeof(NTAccount)).Value.ToLowerInvariant()); }
                catch (IdentityNotMappedException) { names.Add(sid.Value.ToLowerInvariant()); }
                catch (SystemException) { names.Add(sid.Value.ToLowerInvariant()); }
            }
            return Matches(names, allowedGroups)
                ? AccessResult.Granted
                : new AccessResult(false, "Your account is not in a group that may use this app.");
        }
        catch (Exception e) when (e is SystemException)
        {
            DiagnosticLog.Warn("Group memberships could not be read", e);
            return new AccessResult(false, "Your group memberships could not be read.");
        }
    }

    /// <summary>Matching rule, separate for tests: full name, or the part after the backslash.</summary>
    public static bool Matches(IEnumerable<string> userGroups, IReadOnlyList<string> allowed)
    {
        var wanted = allowed.Select(g => g.ToLowerInvariant()).ToHashSet();
        return userGroups.Select(g => g.ToLowerInvariant()).Any(g => wanted.Contains(g) || wanted.Contains(g[(g.LastIndexOf('\\') + 1)..]));
    }
}
