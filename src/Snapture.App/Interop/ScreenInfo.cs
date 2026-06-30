using System.Runtime.InteropServices;
using Snapture.Core.Models;

namespace Snapture.App.Interop;

/// <summary>A monitor's physical-pixel bounds and identity.</summary>
public sealed record MonitorInfo(string Name, bool IsPrimary, CaptureRegion Bounds);

/// <summary>
/// Enumerates monitors and resolves the window under a point — both in physical
/// pixels, matching the coordinate space the capture engine and overlay use.
/// </summary>
public static class ScreenInfo
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var b = s.Bounds; // physical pixels for a per-monitor-DPI-aware process
            result.Add(new MonitorInfo(s.DeviceName, s.Primary,
                new CaptureRegion(b.X, b.Y, b.Width, b.Height)));
        }
        return result;
    }

    public static MonitorInfo MonitorAt(int x, int y)
    {
        var monitors = GetMonitors();
        foreach (var m in monitors)
        {
            if (x >= m.Bounds.X && x < m.Bounds.Right && y >= m.Bounds.Y && y < m.Bounds.Bottom)
                return m;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// Top-level window under the given physical point. Windows are walked in
    /// Z-order (top → bottom), and any window owned by <b>our own process</b> is
    /// skipped — so the full-screen overlay, combo-box dropdown popups, tooltips
    /// and the recording bar are all transparent to selection, and the real
    /// window beneath the overlay is what gets returned. <paramref name="ignore"/>
    /// is an extra safety net (e.g. a specific handle).
    /// </summary>
    public static (nint Handle, CaptureRegion Bounds)? WindowAt(int x, int y, IReadOnlyCollection<nint> ignore)
    {
        var myPid = (uint)Environment.ProcessId;
        nint found = nint.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (ignore.Contains(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == myPid) return true;            // skip all of our own windows
            if (IsCloaked(hwnd)) return true;          // skip cloaked (other virtual desktop / UWP)

            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true; // skip tool windows

            if (!TryGetBounds(hwnd, out var r)) return true;
            if (x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) return true;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;

            found = hwnd;
            return false; // topmost match — stop enumerating
        }, nint.Zero);

        if (found == nint.Zero || !TryGetBounds(found, out var rect))
            return null;

        var bounds = new CaptureRegion(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return bounds.IsEmpty ? null : (found, bounds);
    }

    /// <summary>Prefer the DWM extended frame (excludes the invisible drop-shadow border).</summary>
    private static bool TryGetBounds(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0
            && rect.Right > rect.Left && rect.Bottom > rect.Top)
            return true;
        return GetWindowRect(hwnd, out rect);
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
}
