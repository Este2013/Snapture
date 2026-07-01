using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Snapture.App.Views;

/// <summary>
/// Shared 16px vector glyphs for the capture actions (white, for use on the red
/// action buttons in both the overlay toolbar and the settings window). Each call
/// returns a fresh element, since a WPF visual can only live in one tree.
/// </summary>
internal static class CaptureIcons
{
    /// <summary>"Record": a ring with a filled centre dot.</summary>
    public static UIElement Record()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.6 });
        grid.Children.Add(new Ellipse
        {
            Width = 7, Height = 7, Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    // Four corner brackets in a 16-unit box.
    private const string ScanFrameGeometry =
        "M6 2 H4 A2 2 0 0 0 2 4 V6 M10 2 H12 A2 2 0 0 1 14 4 V6 " +
        "M14 10 V12 A2 2 0 0 1 12 14 H10 M6 14 H4 A2 2 0 0 1 2 12 V10";

    /// <summary>"Scan": corner brackets framing a solid circle.</summary>
    public static UIElement ScanCamera()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(new Path
        {
            Data = Geometry.Parse(ScanFrameGeometry),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        grid.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }
}
