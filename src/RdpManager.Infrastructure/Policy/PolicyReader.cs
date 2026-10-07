using System.Security.AccessControl;
using System.Security.Principal;
using RdpManager.Core.Models;
using RdpManager.Core.Policy;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.Infrastructure.Policy;

/// <summary>Reads the IT policy file. It is only used when it is owned by Administrators or SYSTEM.</summary>
public static class PolicyReader
{
    public static AppPolicy Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return AppPolicy.None(file);
            if (!IsOwnedByAdministrators(file))
            {
                // A standard user could have created this file (ProgramData is writable by Users by default).
                DiagnosticLog.Warn("Policy file ignored: wrong owner");
                return new AppPolicy { File = file, Rejected = "The policy file was ignored because it is not owned by Administrators or SYSTEM." };
            }
            if (Path.GetDirectoryName(file) is { } folder && !IsOwnedByAdministrators(folder))
            {
                // The owner of the folder could replace an admin-owned file.
                DiagnosticLog.Warn("Policy file ignored: folder has the wrong owner");
                return new AppPolicy { File = file, Rejected = "The policy file was ignored because its folder is not owned by Administrators or SYSTEM." };
            }
            var info = new FileInfo(file);
            if (info.Length > 256 * 1024) return new AppPolicy { File = file, Rejected = "The policy file was ignored because it is too large." };
            return PolicyParser.Parse(File.ReadAllText(file), file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Policy file could not be read", e);
            return AppPolicy.None(file);
        }
    }

    /// <summary>True when the file is owned by BUILTIN\Administrators or SYSTEM, i.e. not planted by a standard user.</summary>
    public static bool IsOwnedByAdministrators(string file)
    {
        try
        {
            FileSystemSecurity acl = Directory.Exists(file) ? new DirectoryInfo(file).GetAccessControl(AccessControlSections.Owner) : new FileInfo(file).GetAccessControl(AccessControlSections.Owner);
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return false;
        }
    }
}
