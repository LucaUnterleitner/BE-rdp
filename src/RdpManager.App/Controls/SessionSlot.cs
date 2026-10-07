using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RdpManager.App.Controls;

/// <summary>
/// A plain native child window in the WPF layout. Session host windows (other processes) are made children of
/// this window with SetParent, so WPF positions, sizes and clips them like any other element.
/// </summary>
public sealed partial class SessionSlot : HwndHost
{
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>Raised when the native window was created or changed its size (physical pixels).</summary>
    public event Action? SlotChanged;

    public IntPtr SlotHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // The "Static" class draws nothing (the session covers it); the dark background comes from the parent.
        SlotHandle = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren | WsClipSiblings, 0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(() => SlotChanged?.Invoke());
        return new HandleRef(this, SlotHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        SlotHandle = IntPtr.Zero;
    }

    /// <summary>Client size in physical pixels.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            if (SlotHandle == IntPtr.Zero || !GetClientRect(SlotHandle, out var r)) return (0, 0);
            return (r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => SlotChanged?.Invoke());
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        SlotChanged?.Invoke();
    }
}
