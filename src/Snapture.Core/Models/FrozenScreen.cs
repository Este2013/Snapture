namespace Snapture.Core.Models;

/// <summary>
/// A one-shot screenshot of the whole virtual desktop, grabbed the instant an
/// interactive capture begins — before the selection overlay takes focus.
/// Selecting against this frozen frame lets us keep transient UI (menus,
/// dropdowns, tooltips) that would otherwise vanish the moment another window
/// activates. Pixels are tightly-packed top-down 32-bpp BGRA (stride = Width*4),
/// matching both the snapshot encoder's input and WPF's <c>Bgr32</c> for display.
/// </summary>
public sealed class FrozenScreen
{
    public required byte[] Pixels { get; init; }

    /// <summary>Virtual-desktop top-left in physical pixels (may be negative).</summary>
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Crop the given region (physical px) into a tight BGRA buffer. Returns null
    /// if the region falls outside the frozen bounds. <paramref name="width"/> and
    /// <paramref name="height"/> report the produced dimensions.
    /// </summary>
    public byte[]? Crop(CaptureRegion region, out int width, out int height)
    {
        width = height = 0;
        var r = region.ToEvenDimensions();
        if (r.IsEmpty) return null;

        int x0 = r.X - OriginX, y0 = r.Y - OriginY;
        if (x0 < 0 || y0 < 0 || x0 + r.Width > Width || y0 + r.Height > Height)
            return null;

        int srcStride = Width * 4, dstStride = r.Width * 4;
        var dst = new byte[dstStride * r.Height];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Pixels, (y0 + y) * srcStride + x0 * 4, dst, y * dstStride, dstStride);

        width = r.Width; height = r.Height;
        return dst;
    }
}
