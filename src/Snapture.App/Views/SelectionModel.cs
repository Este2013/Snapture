using Snapture.Core.Models;

namespace Snapture.App.Views;

/// <summary>Which part of the selection a pointer is interacting with.</summary>
public enum SelectionHandle
{
    None,
    Inside,
    TopLeft, Top, TopRight,
    Right,
    BottomRight, Bottom, BottomLeft,
    Left,
}

/// <summary>
/// Holds the custom-selection rectangle in physical pixels and implements the
/// geometry for hit-testing handles, moving, and resizing — all clamped to the
/// virtual desktop. UI-framework agnostic so the logic stays testable.
/// </summary>
public sealed class SelectionModel
{
    private readonly int _vx, _vy, _vRight, _vBottom;

    public SelectionModel(CaptureRegion virtualBounds)
    {
        _vx = virtualBounds.X;
        _vy = virtualBounds.Y;
        _vRight = virtualBounds.Right;
        _vBottom = virtualBounds.Bottom;
    }

    /// <summary>Current rectangle (physical pixels). Empty until a drag begins.</summary>
    public CaptureRegion Region { get; private set; }

    public bool HasSelection => !Region.IsEmpty;

    /// <summary>When true, resizing/inflating preserves <see cref="AspectRatio"/>.</summary>
    public bool AspectLocked { get; private set; }

    /// <summary>Locked width:height ratio (only meaningful while <see cref="AspectLocked"/>).</summary>
    public double AspectRatio { get; private set; } = 1.0;

    /// <summary>Toggle the aspect lock, capturing the current ratio when enabling.</summary>
    public void SetAspectLock(bool on)
    {
        AspectLocked = on;
        if (on && HasSelection && Region.Height > 0)
            AspectRatio = (double)Region.Width / Region.Height;
    }

    private int _anchorX, _anchorY; // fixed corner while drawing

    public void BeginDraw(int x, int y)
    {
        _anchorX = x;
        _anchorY = y;
        Region = new CaptureRegion(x, y, 0, 0);
    }

    public void DrawTo(int x, int y)
    {
        var left = Math.Min(_anchorX, x);
        var top = Math.Min(_anchorY, y);
        var w = Math.Abs(x - _anchorX);
        var h = Math.Abs(y - _anchorY);
        Region = new CaptureRegion(left, top, w, h).ClampTo(_vx, _vy, _vRight, _vBottom);
    }

    public void Set(CaptureRegion region)
    {
        Region = region.ClampTo(_vx, _vy, _vRight, _vBottom);
        // An external set (crop, history, undo) redefines the ratio the lock holds.
        if (AspectLocked && Region.Height > 0)
            AspectRatio = (double)Region.Width / Region.Height;
    }

    /// <summary>Drop the selection (back to no-selection state).</summary>
    public void Clear() => Region = default;

    /// <summary>Grow (positive) or shrink (negative) the rectangle from its centre, clamped.</summary>
    public void Inflate(int delta)
    {
        if (!HasSelection) return;
        int cx = Region.X + Region.Width / 2;
        int cy = Region.Y + Region.Height / 2;

        int newW = Math.Max(2, Region.Width + 2 * delta);
        int newH = AspectLocked && AspectRatio > 0
            ? Math.Max(2, (int)Math.Round(newW / AspectRatio))
            : Math.Max(2, Region.Height + 2 * delta);

        int left = cx - newW / 2, top = cy - newH / 2;
        Region = new CaptureRegion(left, top, newW, newH).ClampTo(_vx, _vy, _vRight, _vBottom);
    }

    /// <summary>Move the whole rectangle by a pixel delta, clamped to bounds.</summary>
    public void MoveBy(int dx, int dy)
    {
        if (!HasSelection) return;
        var x = Math.Clamp(Region.X + dx, _vx, _vRight - Region.Width);
        var y = Math.Clamp(Region.Y + dy, _vy, _vBottom - Region.Height);
        Region = Region with { X = x, Y = y };
    }

    /// <summary>Resize by moving the given handle's edge(s) by a pixel delta.</summary>
    public void ResizeBy(SelectionHandle handle, int dx, int dy)
    {
        if (!HasSelection || handle is SelectionHandle.None or SelectionHandle.Inside)
            return;

        int left = Region.X, top = Region.Y, right = Region.Right, bottom = Region.Bottom;

        bool changesLeft = handle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft;
        bool changesRight = handle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight;
        bool changesTop = handle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight;
        bool changesBottom = handle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight;

        if (changesLeft) left = Math.Clamp(left + dx, _vx, right - 1);
        if (changesRight) right = Math.Clamp(right + dx, left + 1, _vRight);
        if (changesTop) top = Math.Clamp(top + dy, _vy, bottom - 1);
        if (changesBottom) bottom = Math.Clamp(bottom + dy, top + 1, _vBottom);

        if (AspectLocked && AspectRatio > 0)
        {
            bool horizEdge = (changesLeft ^ changesRight) && !changesTop && !changesBottom;
            bool vertEdge = (changesTop ^ changesBottom) && !changesLeft && !changesRight;

            if (vertEdge)
            {
                // Top/bottom edge: derive width from the new height, added equally
                // to both sides (grows about the horizontal centre).
                int cxx = Region.X + Region.Width / 2;
                int newW = Math.Max(2, (int)Math.Round((bottom - top) * AspectRatio));
                left = cxx - newW / 2; right = left + newW;
            }
            else if (horizEdge)
            {
                // Left/right edge: derive height from the new width, centred vertically.
                int cyy = Region.Y + Region.Height / 2;
                int newH = Math.Max(2, (int)Math.Round((right - left) / AspectRatio));
                top = cyy - newH / 2; bottom = top + newH;
            }
            else
            {
                // Corner: derive height from width, anchored at the non-dragged edge.
                int newH = Math.Max(2, (int)Math.Round((right - left) / AspectRatio));
                if (changesTop) top = bottom - newH; else bottom = top + newH;
            }

            left = Math.Max(left, _vx); right = Math.Min(right, _vRight);
            top = Math.Max(top, _vy); bottom = Math.Min(bottom, _vBottom);
        }

        Region = new CaptureRegion(left, top, right - left, bottom - top);
    }

    /// <summary>Hit-test a point against the rectangle's handles (physical px).</summary>
    public SelectionHandle HitTest(int x, int y, int tolerance)
    {
        if (!HasSelection)
            return SelectionHandle.None;

        bool nearLeft = Math.Abs(x - Region.X) <= tolerance;
        bool nearRight = Math.Abs(x - Region.Right) <= tolerance;
        bool nearTop = Math.Abs(y - Region.Y) <= tolerance;
        bool nearBottom = Math.Abs(y - Region.Bottom) <= tolerance;
        bool withinX = x >= Region.X - tolerance && x <= Region.Right + tolerance;
        bool withinY = y >= Region.Y - tolerance && y <= Region.Bottom + tolerance;

        if (nearLeft && nearTop) return SelectionHandle.TopLeft;
        if (nearRight && nearTop) return SelectionHandle.TopRight;
        if (nearLeft && nearBottom) return SelectionHandle.BottomLeft;
        if (nearRight && nearBottom) return SelectionHandle.BottomRight;
        if (nearTop && withinX) return SelectionHandle.Top;
        if (nearBottom && withinX) return SelectionHandle.Bottom;
        if (nearLeft && withinY) return SelectionHandle.Left;
        if (nearRight && withinY) return SelectionHandle.Right;

        if (x > Region.X && x < Region.Right && y > Region.Y && y < Region.Bottom)
            return SelectionHandle.Inside;

        return SelectionHandle.None;
    }
}
