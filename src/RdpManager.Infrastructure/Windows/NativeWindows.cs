using System.Runtime.InteropServices;

namespace RdpManager.Infrastructure.Windows;

/// <summary>Win32 window helpers: bring session windows of other processes to the front.</summary>
public static partial class NativeWindows
{
    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    /// <summary>Visible top-level windows of a process, longest title first (the session window has the host in its title).</summary>
    public static List<IntPtr> WindowsOfProcess(int pid)
    {
        var found = new List<(IntPtr Hwnd, int Len)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)pid && IsWindowVisible(hwnd)) found.Add((hwnd, GetWindowTextLength(hwnd)));
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(f => f.Len).Select(f => f.Hwnd).ToList();
    }

    /// <summary>Brings the mstsc window of a session to the foreground (works because the app itself is in the foreground).</summary>
    public static void FocusProcessWindow(int pid)
    {
        var windows = WindowsOfProcess(pid);
        if (windows.Count == 0) throw new InvalidOperationException("The session window is not open yet.");
        var main = windows[0];
        if (IsIconic(main)) ShowWindow(main, SwRestore);
        SetForegroundWindow(main);
    }

    /// <summary>Brings the console window of a console program (conhost or Windows Terminal) to the foreground.</summary>
    public static void FocusConsoleOf(int pid)
    {
        var hwnd = IntPtr.Zero;
        if (AttachConsole((uint)pid))
        {
            try { hwnd = GetConsoleWindow(); } finally { FreeConsole(); }
        }
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            if (SetForegroundWindow(hwnd)) return;
        }
        throw new InvalidOperationException("The terminal window could not be brought to the front. Switch to it with Alt+Tab.");
    }
}
