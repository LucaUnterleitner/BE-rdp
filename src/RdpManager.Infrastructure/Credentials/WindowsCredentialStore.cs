using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RdpManager.Infrastructure.Credentials;

public sealed record CredentialInfo(bool Saved, string Username = "");

public interface ICredentialStore
{
    CredentialInfo Get(string host);
    void Save(string host, string username, string password);
    bool Delete(string host);
}

/// <summary>
/// Windows Credential Manager: generic credentials "TERMSRV/&lt;host&gt;", which mstsc and the RDP control use and
/// Credential Guard allows. The app never reads a password back; it only checks whether one is saved.
/// </summary>
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref CREDENTIALW credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    public static string TargetFor(string host) => "TERMSRV/" + host.Trim();

    public CredentialInfo Get(string host)
    {
        if (!CredRead(TargetFor(host), CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotFound) return new CredentialInfo(false);
            throw new InvalidOperationException($"Windows Credential Manager could not be read (error {err}).");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(ptr);
            return new CredentialInfo(true, Marshal.PtrToStringUni(cred.UserName) ?? "");
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Save(string host, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new ArgumentException("Username and password are required.");
        var blob = Marshal.StringToCoTaskMemUni(password);
        var target = Marshal.StringToCoTaskMemUni(TargetFor(host));
        var comment = Marshal.StringToCoTaskMemUni("Saved by BearingPoint Remote Desktop");
        var user = Marshal.StringToCoTaskMemUni(username.Trim());
        try
        {
            var cred = new CREDENTIALW
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"The password could not be saved (Windows error {Marshal.GetLastPInvokeError()}). Saving passwords may be blocked by policy.");
        }
        finally
        {
            // The password copy is zeroed before it is freed.
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(comment);
            Marshal.FreeCoTaskMem(user);
        }
    }

    public bool Delete(string host)
    {
        if (CredDelete(TargetFor(host), CredTypeGeneric, 0)) return true;
        if (Marshal.GetLastPInvokeError() == ErrorNotFound) return false;
        throw new InvalidOperationException("The saved password could not be removed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
}
