using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Snapture.App.Views;

/// <summary>
/// Shared 16px capture glyphs. Their colour is bound to the owning button's
/// <see cref="Control.Foreground"/>, so the same icon reads correctly on a red
/// button (white) or a grey footer button (accent), and flips on hover. Each call
/// returns a fresh element (a WPF visual can live in only one tree).
/// </summary>
internal static class CaptureIcons
{
    private static Binding Fg() => new("Foreground")
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
    };

    /// <summary>Record: a filled circle (Fluent "record").</summary>
    public static UIElement Record()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        var dot = new Ellipse
        {
            Width = 13, Height = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetBinding(Shape.FillProperty, Fg());
        grid.Children.Add(dot);
        return grid;
    }

    /// <summary>Stop: a rounded square (Fluent "stop").</summary>
    public static UIElement Stop()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        var square = new Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2.5, RadiusY = 2.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        square.SetBinding(Shape.FillProperty, Fg());
        grid.Children.Add(square);
        return grid;
    }

    // Four corner brackets in a 16-unit box.
    private const string ScanFrameGeometry =
        "M6 2 H4 A2 2 0 0 0 2 4 V6 M10 2 H12 A2 2 0 0 1 14 4 V6 " +
        "M14 10 V12 A2 2 0 0 1 12 14 H10 M6 14 H4 A2 2 0 0 1 2 12 V10";

    /// <summary>Snapshot: corner brackets framing a solid circle.</summary>
    public static UIElement ScanCamera()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        var frame = new Path
        {
            Data = Geometry.Parse(ScanFrameGeometry),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        frame.SetBinding(Shape.StrokeProperty, Fg());
        grid.Children.Add(frame);

        var lens = new Ellipse
        {
            Width = 6, Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        lens.SetBinding(Shape.FillProperty, Fg());
        grid.Children.Add(lens);
        return grid;
    }
}
