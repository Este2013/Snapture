using System.Windows;
using System.Windows.Media;
using Snapture.App.Interop;

namespace Snapture.App.Views;

/// <summary>
/// The dimming layer that sits behind the <see cref="OverlayWindow"/>. It is a
/// plain opaque (hardware-rendered) black window made uniformly translucent by
/// the DWM via <see cref="NativeMethods.MakeDimLayer"/>, with the selection area
/// punched out as a window region. This keeps the expensive part — shading the
/// whole virtual desktop — entirely on the GPU compositor, so moving or resizing
/// the selection only updates a region instead of re-rasterising a desktop-sized
/// semi-transparent fill in software (the old source of the drag lag).
/// </summary>
internal sealed class DimWindow : Window
{
    private readonly int _vx, _vy, _vw, _vh;
    private readonly byte _alpha;

    public DimWindow(int vx, int vy, int vw, int vh, byte alpha)
    {
        _vx = vx; _vy = vy; _vw = vw; _vh = vh;
        _alpha = alpha;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;     // never steal focus from the overlay
        Topmost = true;
        Background = Brushes.Black; // DWM applies the uniform alpha on top of this

        // SourceInitialized fires once the HWND exists but before the first paint,
        // so the uniform alpha is in place before the black ever hits the screen.
        SourceInitialized += (_, _) =>
        {
            NativeMethods.SetWindowBoundsPhysical(this, _vx, _vy, _vw, _vh);
            NativeMethods.MakeDimLayer(this, _alpha);
        };
    }

    /// <summary>Show the dim everywhere (no selection yet).</summary>
    public void ClearHole() =>
        NativeMethods.SetDesktopHole(this, _vx, _vy, _vw, _vh, null);

    /// <summary>Punch the selection rectangle (physical px) out of the dim.</summary>
    public void SetHole(int px, int py, int pw, int ph) =>
        NativeMethods.SetDesktopHole(this, _vx, _vy, _vw, _vh, (px, py, pw, ph));
}
