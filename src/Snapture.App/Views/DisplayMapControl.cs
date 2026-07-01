using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Snapture.App.Interop;

namespace Snapture.App.Views;

/// <summary>
/// A miniature, Windows-Settings-style map of every monitor, shown centred on the
/// main display while picking a display to record. The monitor the physical mouse
/// is currently on is outlined and carries a live cursor dot; clicking any tile
/// records that display. All input coordinates are physical pixels, matching the
/// rest of the overlay.
/// </summary>
internal sealed class DisplayMapControl : Border
{
    private const double MaxContentWidth = 560;
    private const double MaxContentHeight = 300;
    private const double GapPx = 6; // visual gap between tiles (DIP), like Windows Settings

    private static readonly Brush TileFill = Frozen(0xFF3A3A3D);
    private static readonly Brush TileFillHover = Frozen(0xFF4A4A4E);
    private static readonly Brush TileStroke = Frozen(0xFF5A5A5E);
    private static readonly Brush AccentStroke = Frozen(0xFF60CDFF);
    private static readonly Brush TextSecondary = Frozen(0xFFBBBBBB);

    private readonly Canvas _canvas = new();
    private readonly List<(MonitorInfo Mon, Border Tile)> _tiles = new();
    private readonly Ellipse _badge;
    private Border? _hoveredTile;

    private int _minX, _minY;
    private double _scale = 1;

    public DisplayMapControl()
    {
        Background = Frozen(0xFF2B2B2B);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(16);
        BorderBrush = Frozen(0x33FFFFFF);
        BorderThickness = new Thickness(1);
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black };

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = "Click a display to record",
            Foreground = TextSecondary,
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 10),
        });
        root.Children.Add(_canvas);
        Child = root;

        _badge = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = AccentStroke,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Panel.SetZIndex(_badge, 100);

        // Swallow clicks on the card itself so they don't fall through to the
        // overlay (which would confirm whatever monitor is under the cursor).
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    public event Action<MonitorInfo>? DisplayClicked;

    /// <summary>Raised when the pointer enters/leaves a display tile (null on leave).</summary>
    public event Action<MonitorInfo?>? DisplayHovered;

    public void Build(IReadOnlyList<MonitorInfo> monitors)
    {
        _canvas.Children.Clear();
        _tiles.Clear();
        _hoveredTile = null;
        if (monitors.Count == 0) return;

        _minX = monitors.Min(m => m.Bounds.X);
        _minY = monitors.Min(m => m.Bounds.Y);
        int maxX = monitors.Max(m => m.Bounds.Right);
        int maxY = monitors.Max(m => m.Bounds.Bottom);
        double bbW = Math.Max(1, maxX - _minX);
        double bbH = Math.Max(1, maxY - _minY);
        _scale = Math.Min(MaxContentWidth / bbW, MaxContentHeight / bbH);

        _canvas.Width = bbW * _scale;
        _canvas.Height = bbH * _scale;

        foreach (var m in monitors)
        {
            var tile = CreateTile(m);
            _tiles.Add((m, tile));
            _canvas.Children.Add(tile);
        }
        _canvas.Children.Add(_badge);
    }

    /// <summary>Position the live cursor dot in the tile the physical cursor is over.</summary>
    public void UpdateMouse(int physX, int physY)
    {
        bool onAMonitor = _tiles.Any(t =>
            physX >= t.Mon.Bounds.X && physX < t.Mon.Bounds.Right &&
            physY >= t.Mon.Bounds.Y && physY < t.Mon.Bounds.Bottom);

        if (!onAMonitor)
        {
            _badge.Visibility = Visibility.Collapsed;
            return;
        }

        _badge.Visibility = Visibility.Visible;
        Canvas.SetLeft(_badge, (physX - _minX) * _scale - _badge.Width / 2);
        Canvas.SetTop(_badge, (physY - _minY) * _scale - _badge.Height / 2);
    }

    private Border CreateTile(MonitorInfo m)
    {
        double x = (m.Bounds.X - _minX) * _scale + GapPx / 2;
        double y = (m.Bounds.Y - _minY) * _scale + GapPx / 2;
        double w = Math.Max(1, m.Bounds.Width * _scale - GapPx);
        double h = Math.Max(1, m.Bounds.Height * _scale - GapPx);

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = DisplayNumber(m),
            Foreground = Brushes.White,
            FontSize = Math.Clamp(Math.Min(w, h) * 0.4, 14, 40),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{m.Bounds.Width}×{m.Bounds.Height}" + (m.IsPrimary ? "  ·  Main" : ""),
            Foreground = TextSecondary,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var tile = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(6),
            Background = TileFill,
            BorderBrush = TileStroke,
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Child = content,
        };
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);

        // Hovering a tile highlights it (accent border) and previews that display.
        tile.MouseEnter += (_, _) =>
        {
            if (_hoveredTile is not null && !ReferenceEquals(_hoveredTile, tile))
            {
                _hoveredTile.BorderBrush = TileStroke;
                _hoveredTile.Background = TileFill;
            }
            _hoveredTile = tile;
            tile.BorderBrush = AccentStroke;
            tile.Background = TileFillHover;
            DisplayHovered?.Invoke(m);
        };
        tile.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(_hoveredTile, tile))
            {
                tile.BorderBrush = TileStroke;
                tile.Background = TileFill;
                _hoveredTile = null;
                DisplayHovered?.Invoke(null);
            }
        };
        tile.MouseLeftButtonDown += (_, e) => { e.Handled = true; DisplayClicked?.Invoke(m); };
        return tile;
    }

    /// <summary>"\\.\DISPLAY2" → "2"; Windows Settings numbers displays the same way.</summary>
    private static string DisplayNumber(MonitorInfo m)
    {
        var name = m.Name;
        for (int i = name.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(name[i]))
                return name[(i + 1)..];
        }
        return name;
    }

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}
