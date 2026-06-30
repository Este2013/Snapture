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

    public void Set(CaptureRegion region) =>
        Region = region.ClampTo(_vx, _vy, _vRight, _vBottom);

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

        if (handle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft)
            left = Math.Clamp(left + dx, _vx, right - 1);
        if (handle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight)
            right = Math.Clamp(right + dx, left + 1, _vRight);
        if (handle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight)
            top = Math.Clamp(top + dy, _vy, bottom - 1);
        if (handle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight)
            bottom = Math.Clamp(bottom + dy, top + 1, _vBottom);

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
