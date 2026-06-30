using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Snapture.App.Interop;

/// <summary>
/// Win32 helpers for positioning the overlay in physical pixels and reading the
/// virtual-desktop geometry. WPF positions windows in DIPs which is awkward for
/// a pixel-exact full-desktop overlay, so we drive placement through SetWindowPos.
/// </summary>
internal static class NativeMethods
{
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    // Extended window styles.
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080; // keep off alt-tab / taskbar
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020; // click-through

    private const uint LWA_ALPHA = 0x2;
    private const int RGN_DIFF = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(nint hrgnDst, nint hrgnSrc1, nint hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint ho);

    /// <summary>Virtual desktop bounds in physical pixels (origin may be negative).</summary>
    public static (int X, int Y, int Width, int Height) GetVirtualScreenPhysical() =>
    (
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN)
    );

    /// <summary>Move/size a window using physical-pixel coordinates, topmost, no activate.</summary>
    public static void SetWindowBoundsPhysical(Window window, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Mark a window as a tool window so it stays out of alt-tab/taskbar.</summary>
    public static void MarkToolWindow(Window window, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        if (noActivate)
            ex |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>
    /// Turn a window into a click-through layered window with uniform opacity,
    /// so the DWM composites its transparency on the GPU instead of WPF doing a
    /// software per-pixel blit. Used by the dim layer behind the selection overlay.
    /// </summary>
    public static void MakeDimLayer(Window window, byte alpha)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>
    /// Carve a rectangular hole out of <paramref name="window"/> using a window
    /// region (cheap, GPU-composited). Coordinates are physical pixels; the window
    /// is assumed to span the virtual desktop starting at (vx, vy). Pass a null
    /// hole to clear the region so the whole window shows.
    /// </summary>
    public static void SetDesktopHole(Window window, int vx, int vy, int vw, int vh,
        (int X, int Y, int W, int H)? hole)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;

        if (hole is not { } h)
        {
            SetWindowRgn(hwnd, nint.Zero, true); // no hole → whole window visible
            return;
        }

        var full = CreateRectRgn(0, 0, vw, vh);
        int lx = h.X - vx, ly = h.Y - vy;
        var cut = CreateRectRgn(lx, ly, lx + h.W, ly + h.H);
        CombineRgn(full, full, cut, RGN_DIFF);
        DeleteObject(cut);
        // On success the system owns 'full'; don't delete it.
        if (SetWindowRgn(hwnd, full, true) == 0)
            DeleteObject(full);
    }

    /// <summary>Place <paramref name="below"/> directly beneath <paramref name="above"/> in z-order.</summary>
    public static void PlaceDirectlyBelow(Window below, Window above)
    {
        var b = new WindowInteropHelper(below).Handle;
        var a = new WindowInteropHelper(above).Handle;
        SetWindowPos(b, a, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
