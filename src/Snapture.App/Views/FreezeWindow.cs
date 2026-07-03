using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snapture.App.Interop;
using Snapture.Core.Models;

namespace Snapture.App.Views;

/// <summary>
/// An opaque, full-virtual-desktop backdrop showing the frozen screenshot taken
/// when an interactive snapshot began. It sits directly below the <see cref="DimWindow"/>,
/// so the selection UI dims and reveals the frozen frame exactly as it would the
/// live desktop — but transient popups (menus, dropdowns) stay visible and
/// capturable because they're baked into these pixels.
/// </summary>
internal sealed class FreezeWindow : Window
{
    private readonly int _vx, _vy, _vw, _vh;

    public FreezeWindow(FrozenScreen frozen)
    {
        _vx = frozen.OriginX; _vy = frozen.OriginY; _vw = frozen.Width; _vh = frozen.Height;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;   // never steal focus from the overlay
        Topmost = true;

        // GDI leaves the alpha byte at 0, so display with Bgr32 (alpha ignored)
        // to avoid a fully transparent backdrop.
        var bmp = BitmapSource.Create(frozen.Width, frozen.Height, 96, 96,
            PixelFormats.Bgr32, null, frozen.Pixels, frozen.Width * 4);
        bmp.Freeze();
        Content = new Image { Source = bmp, Stretch = Stretch.Fill };

        SourceInitialized += (_, _) =>
        {
            NativeMethods.SetWindowBoundsPhysical(this, _vx, _vy, _vw, _vh);
            NativeMethods.MarkToolWindow(this, noActivate: true);
        };
    }
}
