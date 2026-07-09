using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snapture.App.Interop;

/// <summary>Fetches a window's icon as a WPF <see cref="ImageSource"/> for the crop picker.</summary>
internal static class WindowIcon
{
    private const int WM_GETICON = 0x007F;
    private const int ICON_SMALL2 = 2, ICON_BIG = 1;
    private const int GCLP_HICON = -14, GCLP_HICONSM = -34;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, int msg, nint wParam, nint lParam,
        uint flags, uint timeoutMs, out nint result);

    private static nint GetIcon(nint hwnd, int type)
    {
        // Timeout so a hung window can't stall the picker.
        return SendMessageTimeoutW(hwnd, WM_GETICON, type, 0, SMTO_ABORTIFHUNG, 200, out var res) == 0 ? 0 : res;
    }

    public static ImageSource? For(nint hwnd)
    {
        nint h = GetClassLongPtr(hwnd, GCLP_HICONSM); // non-blocking first
        if (h == 0) h = GetIcon(hwnd, ICON_SMALL2);
        if (h == 0) h = GetClassLongPtr(hwnd, GCLP_HICON);
        if (h == 0) h = GetIcon(hwnd, ICON_BIG);
        if (h == 0) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }
}
