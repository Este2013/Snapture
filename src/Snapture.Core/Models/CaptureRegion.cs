namespace Snapture.Core.Models;

/// <summary>
/// A capture rectangle in <b>physical</b> (device) pixel coordinates within the
/// virtual desktop. The virtual desktop origin may be negative when secondary
/// monitors sit to the left of / above the primary one, so signed ints are used.
/// </summary>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>
    /// Video encoders require even dimensions for most pixel formats (yuv420p).
    /// Round width/height down to the nearest even number, keeping the origin.
    /// </summary>
    public CaptureRegion ToEvenDimensions()
    {
        var w = Width - (Width % 2);
        var h = Height - (Height % 2);
        return this with { Width = w, Height = h };
    }

    /// <summary>Clamp this region so it stays inside the given bounds.</summary>
    public CaptureRegion ClampTo(int boundsX, int boundsY, int boundsRight, int boundsBottom)
    {
        var x = Math.Clamp(X, boundsX, boundsRight);
        var y = Math.Clamp(Y, boundsY, boundsBottom);
        var right = Math.Clamp(Right, x, boundsRight);
        var bottom = Math.Clamp(Bottom, y, boundsBottom);
        return new CaptureRegion(x, y, right - x, bottom - y);
    }

    public override string ToString() => $"{Width}x{Height} @ ({X},{Y})";
}
