using System.Runtime.InteropServices;
using Snapture.Core.Models;

namespace Snapture.Core.Capture;

/// <summary>
/// Captures a screen region via GDI <c>BitBlt</c> from the desktop DC into a
/// reusable top-down 32-bpp DIB section, then copies the pixels into a pooled
/// <see cref="Frame"/>. Supports arbitrary regions across the virtual desktop
/// and optional cursor compositing.
/// </summary>
/// <remarks>
/// GDI is the v0 backend: dependency-free and reliable for desktop/window/region
/// recording. It does not capture hardware-protected surfaces and tops out at
/// moderate frame rates; a Windows.Graphics.Capture backend can replace it
/// behind <see cref="IFrameSource"/> without changing the pipeline.
/// </remarks>
public sealed class GdiFrameSource : IFrameSource
{
    private readonly CaptureRegion _region;
    private readonly bool _captureCursor;

    private nint _screenDc;
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private nint _bits; // pointer to the DIB pixel data (top-down BGRA)
    private bool _disposed;

    public GdiFrameSource(CaptureTarget target, bool captureCursor)
    {
        _region = target.Region.ToEvenDimensions();
        if (_region.IsEmpty)
            throw new ArgumentException("Capture region is empty.", nameof(target));

        _captureCursor = captureCursor;
        Width = _region.Width;
        Height = _region.Height;
        Allocate();
    }

    public int Width { get; }
    public int Height { get; }

    private void Allocate()
    {
        _screenDc = NativeMethods.GetDC(nint.Zero);
        if (_screenDc == nint.Zero)
            throw new InvalidOperationException("Failed to get desktop device context.");

        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);

        // Negative height => top-down DIB, so row 0 is the top row (what ffmpeg
        // wants for rawvideo bgra).
        var bmi = new NativeMethods.BITMAPINFO
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        _dib = NativeMethods.CreateDIBSection(_memDc, ref bmi, 0, out _bits, nint.Zero, 0);
        if (_dib == nint.Zero || _bits == nint.Zero)
            throw new InvalidOperationException("Failed to create DIB section.");

        _oldBitmap = NativeMethods.SelectObject(_memDc, _dib);
    }

    public Frame? Capture(TimeSpan timestamp)
    {
        if (_disposed)
            return null;

        // SRCCOPY only — deliberately no CAPTUREBLT. CAPTUREBLT forces Windows to
        // hide and redraw the hardware cursor on every BitBlt, which makes the
        // on-screen cursor flicker (appearing to jump to 0,0) during a recording.
        // The DWM already composites normal and layered windows into the screen
        // DC, so SRCCOPY captures them fine, and we draw the cursor ourselves below.
        const int SRCCOPY = 0x00CC0020;

        var ok = NativeMethods.BitBlt(
            _memDc, 0, 0, Width, Height,
            _screenDc, _region.X, _region.Y, SRCCOPY);
        if (!ok)
            return null;

        if (_captureCursor)
            DrawCursor();

        var frame = Frame.Rent(Width, Height, timestamp);
        var dst = frame.GetWritableSpan();
        unsafe
        {
            // DIB is tightly packed (width * 4) because biWidth is a multiple of
            // 2 and biBitCount is 32, so stride == frame stride.
            var src = new ReadOnlySpan<byte>((void*)_bits, dst.Length);
            src.CopyTo(dst);
        }

        return frame;
    }

    private void DrawCursor()
    {
        var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || ci.flags != NativeMethods.CURSOR_SHOWING)
            return;

        if (!NativeMethods.GetIconInfo(ci.hCursor, out var ii))
            return;

        try
        {
            // ptScreenPos is the hotspot location in virtual-desktop pixels.
            var x = ci.ptScreenPos.x - _region.X - ii.xHotspot;
            var y = ci.ptScreenPos.y - _region.Y - ii.yHotspot;
            NativeMethods.DrawIconEx(_memDc, x, y, ci.hCursor, 0, 0, 0, nint.Zero,
                NativeMethods.DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask != nint.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != nint.Zero) NativeMethods.DeleteObject(ii.hbmColor);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_memDc != nint.Zero)
        {
            if (_oldBitmap != nint.Zero)
                NativeMethods.SelectObject(_memDc, _oldBitmap);
            NativeMethods.DeleteDC(_memDc);
        }
        if (_dib != nint.Zero)
            NativeMethods.DeleteObject(_dib);
        if (_screenDc != nint.Zero)
            NativeMethods.ReleaseDC(nint.Zero, _screenDc);

        _memDc = _dib = _screenDc = _oldBitmap = _bits = nint.Zero;
    }

    private static class NativeMethods
    {
        public const int CURSOR_SHOWING = 0x00000001;
        public const int DI_NORMAL = 0x0003;

        [DllImport("user32.dll")] public static extern nint GetDC(nint hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hWnd, nint hDC);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(nint hdc, int x, int y, nint hIcon,
            int cxWidth, int cyWidth, int istepIfAniCur, nint hbrFlickerFreeDraw, int diFlags);

        [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint ho);
        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy,
            nint hdcSrc, int x1, int y1, int rop);
        [DllImport("gdi32.dll")]
        public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage,
            out nint ppvBits, nint hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public nint hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public nint hbmMask;
            public nint hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
            // Color table placeholder (unused for 32bpp BI_RGB).
            public int colors;
        }
    }
}
