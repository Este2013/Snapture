using System.Windows;
using System.Windows.Automation;
using Snapture.Core.Models;

namespace Snapture.App.Interop;

/// <summary>
/// Resolves the nested "logical areas" (UI Automation elements) under a screen
/// point, so the custom selection can snap to real widgets of the window below —
/// the same accessibility tree that test-automation tools and Snipaste rely on.
/// </summary>
/// <remarks>
/// Rather than <c>AutomationElement.FromPoint</c> (which would return our own
/// transparent overlay), we find the window beneath the overlay with
/// <see cref="ScreenInfo.WindowAt"/> and walk <em>down</em> its control tree,
/// picking the smallest child that still contains the point at each level. The
/// result is ordered deepest-first (index 0 = the tightest area at the point),
/// up through the containing window — exactly the chain the scroll-to-parent
/// gesture walks. Calls are best-effort: UI Automation throws freely for
/// transient/again-unavailable elements, so everything is wrapped and degrades
/// to an empty chain. Intended to run off the UI thread.
/// </remarks>
internal static class LogicalAreaProbe
{
    private const int MaxDepth = 16;
    private const int MaxChildrenPerLevel = 800;
    private const int NearEqualTolerancePx = 3;

    public static IReadOnlyList<CaptureRegion> AreasAt(int physX, int physY, params nint[] ignore)
    {
        try
        {
            var below = ScreenInfo.WindowAt(physX, physY, ignore);
            if (below is not { } w)
                return Array.Empty<CaptureRegion>();

            var root = AutomationElement.FromHandle(w.Handle);
            if (root is null)
                return Array.Empty<CaptureRegion>();

            var pt = new Point(physX, physY);
            var topDown = new List<CaptureRegion>();
            AddRect(topDown, root);

            var current = root;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                var next = SmallestChildContaining(current, pt);
                if (next is null)
                    break;
                AddRect(topDown, next);
                current = next;
            }

            topDown.Reverse(); // deepest first
            return Dedupe(topDown);
        }
        catch
        {
            return Array.Empty<CaptureRegion>();
        }
    }

    private static AutomationElement? SmallestChildContaining(AutomationElement parent, Point pt)
    {
        AutomationElementCollection children;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.TreeFilter = Automation.ControlViewCondition;
        try
        {
            using (cache.Activate())
                children = parent.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
        }
        catch
        {
            return null;
        }

        AutomationElement? best = null;
        double bestArea = double.MaxValue;
        int scanned = 0;

        foreach (AutomationElement child in children)
        {
            if (++scanned > MaxChildrenPerLevel)
                break;

            Rect r;
            try { r = child.Cached.BoundingRectangle; }
            catch { continue; }

            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || !r.Contains(pt))
                continue;

            var area = r.Width * r.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = child;
            }
        }

        return best;
    }

    private static void AddRect(List<CaptureRegion> list, AutomationElement element)
    {
        try
        {
            var r = (Rect)element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
            if (!r.IsEmpty && r.Width >= 1 && r.Height >= 1)
                list.Add(new CaptureRegion(
                    (int)Math.Round(r.X), (int)Math.Round(r.Y),
                    (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
        }
        catch { /* element went away mid-walk */ }
    }

    private static IReadOnlyList<CaptureRegion> Dedupe(List<CaptureRegion> chain)
    {
        var result = new List<CaptureRegion>(chain.Count);
        foreach (var r in chain)
        {
            if (result.Count > 0 && NearEqual(result[^1], r))
                continue;
            result.Add(r);
        }
        return result;
    }

    public static bool NearEqual(CaptureRegion a, CaptureRegion b)
    {
        const int t = NearEqualTolerancePx;
        return Math.Abs(a.X - b.X) <= t && Math.Abs(a.Y - b.Y) <= t
            && Math.Abs(a.Width - b.Width) <= t && Math.Abs(a.Height - b.Height) <= t;
    }
}
