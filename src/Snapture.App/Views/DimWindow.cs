using System.Windows;
using System.Windows.Media;
using Snapture.App.Interop;

namespace Snapture.App.Views;

/// <summary>
/// The dimming layer that sits behind the <see cref="OverlayWindow"/>: a
/// translucent black window spanning the virtual desktop, with the selection
/// area punched out as a window region.
/// </summary>
/// <remarks>
/// The translucency is a plain semi-transparent WPF background (an
/// <see cref="Window.AllowsTransparency"/> window) rather than a layered-window
/// alpha — <c>SetLayeredWindowAttributes</c> is silently ignored on a
/// hardware-rendered WPF window and leaves it fully opaque. The performance win
/// still holds because this window's <em>content</em> is static: WPF does its one
/// software composite when shown, and thereafter moving/resizing the selection
/// only updates the window region (cheap, GPU-composited) instead of
/// re-rasterising a desktop-sized fill on every drag like the old overlay did.
/// </remarks>
internal sealed class DimWindow : Window
{
    private readonly int _vx, _vy, _vw, _vh;

    public DimWindow(int vx, int vy, int vw, int vh, byte alpha)
    {
        _vx = vx; _vy = vy; _vw = vw; _vh = vh;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;     // never steal focus from the overlay
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

        SourceInitialized += (_, _) =>
        {
            NativeMethods.SetWindowBoundsPhysical(this, _vx, _vy, _vw, _vh);
            NativeMethods.MarkToolWindow(this, noActivate: true);
        };
    }

    /// <summary>Show the dim everywhere (no selection yet).</summary>
    public void ClearHole() =>
        NativeMethods.SetDesktopHole(this, _vx, _vy, _vw, _vh, null);

    /// <summary>Punch the selection rectangle (physical px) out of the dim.</summary>
    public void SetHole(int px, int py, int pw, int ph) =>
        NativeMethods.SetDesktopHole(this, _vx, _vy, _vw, _vh, (px, py, pw, ph));
}
