using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Snapture.App.Interop;
using Snapture.Core.Models;
using CaptureMode = Snapture.Core.Models.CaptureMode; // disambiguate from System.Windows.Input.CaptureMode

namespace Snapture.App.Views;

/// <summary>
/// Full-virtual-desktop dim overlay for picking the capture area. The toolbar is
/// hosted inside this window (rendered above the dim) so it is always operable,
/// and the recording controls plus selection geometry share one input surface.
/// Works in physical pixels internally so the produced <see cref="CaptureTarget"/>
/// maps 1:1 to what the GDI capture engine grabs.
/// </summary>
public partial class OverlayWindow : Window
{
    private sealed record FormatItem(string Label, OutputFormat Format) { public override string ToString() => Label; }

    private const int HandleTolerancePx = 10;

    private readonly List<Rectangle> _handles = new();
    private SelectionModel _model = null!;

    private int _vx, _vy, _vw, _vh;     // virtual desktop, physical px
    private double _scale = 1.0;         // physical px per DIP
    private bool _loaded;
    private bool _suppressModeEvents;

    private CaptureMode _mode;
    private bool _dragging;
    private bool _drawingNew;
    private SelectionHandle _activeHandle = SelectionHandle.None;
    private SelectionHandle _mouseHeldHandle = SelectionHandle.None;
    private int _lastPx, _lastPy;
    private int _cursorPx, _cursorPy;

    private CaptureRegion _hoverRegion;
    private nint _hoverWindow;
    private string? _hoverLabel;

    // The dim is a separate GPU-composited window behind this one.
    private DimWindow? _dim;
    private const byte DimAlpha = 0x8C; // matches the previous #8C000000 shade

    // Display-mode picker: a Windows-Settings-style map of all monitors.
    private DisplayMapControl? _displayMap;

    // Visual updates are coalesced to one per rendered frame: mouse-move events
    // fire far faster than this full-desktop layered window can repaint, so doing
    // the work on every move backs up the render queue and the overlay lags behind.
    private bool _visualsDirty;
    private bool _renderHooked;

    // Toolbar dragging (move it out of the way; not persisted across sessions).
    private bool _toolbarDragging;
    private bool _toolbarMoved;
    private Point _toolbarDragOrigin;
    private double _toolbarStartLeft, _toolbarStartTop;

    public OverlayWindow(CaptureMode mode, OutputFormat format)
    {
        InitializeComponent();
        _mode = mode;
        CreateHandles();

        FormatCombo.ItemsSource = new[]
        {
            new FormatItem("MP4", OutputFormat.Mp4),
            new FormatItem("WebP", OutputFormat.WebP),
            new FormatItem("GIF", OutputFormat.Gif),
        };
        FormatCombo.SelectedItem = ((IEnumerable<FormatItem>)FormatCombo.ItemsSource).First(f => f.Format == format);

        WireToolbar(mode);

        Loaded += OnLoaded;
        SizeChanged += (_, _) => { UpdateVisuals(); PositionToolbar(); UpdateDisplayMap(); };
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        KeyDown += OnKeyDown;
    }

    public nint ToolbarHandle { get; set; }

    public event Action<CaptureTarget?>? TargetChanged;
    public event Action<OutputFormat>? FormatChanged;
    public event Action? Confirmed;
    public event Action? Cancelled;

    private void WireToolbar(CaptureMode mode)
    {
        _suppressModeEvents = true;
        ModeDisplay.IsChecked = mode == CaptureMode.Display;
        ModeWindow.IsChecked = mode == CaptureMode.Window;
        ModeCustom.IsChecked = mode == CaptureMode.Custom;
        _suppressModeEvents = false;

        ModeDisplay.Checked += (_, _) => OnModePicked(CaptureMode.Display);
        ModeWindow.Checked += (_, _) => OnModePicked(CaptureMode.Window);
        ModeCustom.Checked += (_, _) => OnModePicked(CaptureMode.Custom);

        FormatCombo.SelectionChanged += (_, _) =>
        {
            if (FormatCombo.SelectedItem is FormatItem f)
                FormatChanged?.Invoke(f.Format);
        };

        RecordButton.Click += (_, _) => { if (GetCurrentTarget() is not null) Confirmed?.Invoke(); };
        CancelButton.Click += (_, _) => Cancelled?.Invoke();
        Toolbar.SizeChanged += (_, _) => PositionToolbar();

        // Drag the toolbar by any empty area; child controls handle their own
        // clicks so this only fires on the toolbar background/padding.
        Toolbar.MouseLeftButtonDown += OnToolbarMouseDown;
        Toolbar.MouseMove += OnToolbarMouseMove;
        Toolbar.MouseLeftButtonUp += OnToolbarMouseUp;
    }

    private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e)
    {
        _toolbarDragging = true;
        _toolbarDragOrigin = e.GetPosition(RootCanvas);
        _toolbarStartLeft = Canvas.GetLeft(Toolbar);
        _toolbarStartTop = Canvas.GetTop(Toolbar);
        Toolbar.CaptureMouse();
        e.Handled = true; // don't let the window start a new selection
    }

    private void OnToolbarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_toolbarDragging) return;
        var p = e.GetPosition(RootCanvas);
        Canvas.SetLeft(Toolbar, _toolbarStartLeft + (p.X - _toolbarDragOrigin.X));
        Canvas.SetTop(Toolbar, _toolbarStartTop + (p.Y - _toolbarDragOrigin.Y));
        _toolbarMoved = true; // stop auto-recentring once the user has moved it
        e.Handled = true;
    }

    private void OnToolbarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_toolbarDragging) return;
        _toolbarDragging = false;
        Toolbar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnModePicked(CaptureMode mode)
    {
        if (_suppressModeEvents) return;
        _mode = mode;
        if (mode != CaptureMode.Custom)
            UpdateHoverTarget(_cursorPx, _cursorPy);
        UpdateVisuals();
        UpdateDisplayMap();
        RaiseTarget();
    }

    public CaptureTarget? GetCurrentTarget()
    {
        switch (_mode)
        {
            case CaptureMode.Custom:
                if (_model is null || !_model.HasSelection || _model.Region.ToEvenDimensions().IsEmpty)
                    return null;
                return new CaptureTarget { Mode = CaptureMode.Custom, Region = _model.Region, Label = _model.Region.ToString() };
            case CaptureMode.Display:
                if (_hoverRegion.IsEmpty) return null;
                return new CaptureTarget { Mode = CaptureMode.Display, Region = _hoverRegion, Label = _hoverLabel };
            case CaptureMode.Window:
                if (_hoverRegion.IsEmpty || _hoverWindow == nint.Zero) return null;
                return new CaptureTarget { Mode = CaptureMode.Window, Region = _hoverRegion, WindowHandle = _hoverWindow, Label = _hoverLabel };
            default:
                return null;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        (_vx, _vy, _vw, _vh) = NativeMethods.GetVirtualScreenPhysical();

        var src = (HwndSource?)PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is not null)
            _scale = src.CompositionTarget.TransformToDevice.M11;
        if (_scale <= 0) _scale = 1.0;

        NativeMethods.SetWindowBoundsPhysical(this, _vx, _vy, _vw, _vh);
        NativeMethods.MarkToolWindow(this, noActivate: false);

        _model = new SelectionModel(new CaptureRegion(_vx, _vy, _vw, _vh));

        Activate();
        Focus();
        Keyboard.Focus(this);

        if (NativeMethods.GetCursorPos(out var p))
        {
            _cursorPx = p.X; _cursorPy = p.Y;
            if (_mode != CaptureMode.Custom)
                UpdateHoverTarget(_cursorPx, _cursorPy);
        }

        _loaded = true;

        // Dim layer behind us. ShowActivated=false keeps our keyboard focus; we
        // then drop it directly below this window so our chrome stays on top.
        _dim = new DimWindow(_vx, _vy, _vw, _vh, DimAlpha);
        _dim.Show();
        NativeMethods.PlaceDirectlyBelow(_dim, this);
        _dim.ClearHole();

        if (!_renderHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            Closed += OnClosed;
            _renderHooked = true;
        }

        // Build the display picker once; visibility tracks Display mode. Inserted
        // at the bottom of the z-order so the toolbar and selection chrome stay on top.
        _displayMap = new DisplayMapControl { Visibility = Visibility.Collapsed };
        _displayMap.DisplayClicked += OnDisplayPicked;
        RootCanvas.Children.Insert(0, _displayMap);
        _displayMap.Build(ScreenInfo.GetMonitors());

        UpdateVisualsCore();
        PositionToolbar();
        UpdateDisplayMap();
    }

    private void OnDisplayPicked(MonitorInfo m)
    {
        _hoverRegion = m.Bounds;
        _hoverWindow = nint.Zero;
        _hoverLabel = $"{m.Bounds.Width}x{m.Bounds.Height}" + (m.IsPrimary ? " (primary)" : "");
        Confirmed?.Invoke();
    }

    /// <summary>Toggle, position and refresh the display map for the current mode.</summary>
    private void UpdateDisplayMap()
    {
        if (_displayMap is null) return;

        if (_mode != CaptureMode.Display)
        {
            _displayMap.Visibility = Visibility.Collapsed;
            return;
        }

        _displayMap.Visibility = Visibility.Visible;
        _displayMap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (px, py, pw, ph) = PrimaryMonitorDip();
        Canvas.SetLeft(_displayMap, px + (pw - _displayMap.DesiredSize.Width) / 2);
        Canvas.SetTop(_displayMap, py + (ph - _displayMap.DesiredSize.Height) / 2);
        _displayMap.UpdateMouse(_cursorPx, _cursorPy);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        var dim = _dim; _dim = null;
        try { dim?.Close(); } catch { }
    }

    /// <summary>Flush a pending visual update at most once per rendered frame.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_visualsDirty) return;
        _visualsDirty = false;
        UpdateVisualsCore();
    }

    // ---- coordinate conversion -------------------------------------------

    private (int X, int Y) ToPhysical(Point dip) =>
        ((int)Math.Round(_vx + dip.X * _scale), (int)Math.Round(_vy + dip.Y * _scale));

    private double PhysXToDip(int px) => (px - _vx) / _scale;
    private double PhysYToDip(int py) => (py - _vy) / _scale;

    // ---- mouse ------------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var (px, py) = ToPhysical(e.GetPosition(RootCanvas));
        _lastPx = px; _lastPy = py;

        if (_mode != CaptureMode.Custom)
        {
            UpdateHoverTarget(px, py);
            if (GetCurrentTarget() is not null)
                Confirmed?.Invoke();
            return;
        }

        var handle = _model.HitTest(px, py, (int)(HandleTolerancePx * _scale));
        if (_model.HasSelection && handle != SelectionHandle.None)
        {
            _dragging = true; _drawingNew = false;
            _activeHandle = handle; _mouseHeldHandle = handle;
        }
        else
        {
            _model.BeginDraw(px, py);
            _drawingNew = true; _dragging = true;
            _activeHandle = SelectionHandle.BottomRight;
            _mouseHeldHandle = SelectionHandle.None;
            HintBadge.Visibility = Visibility.Collapsed;
        }

        CaptureMouse();
        UpdateVisuals();
        RaiseTarget();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var (px, py) = ToPhysical(e.GetPosition(RootCanvas));
        _cursorPx = px; _cursorPy = py;

        if (_mode != CaptureMode.Custom)
        {
            UpdateHoverTarget(px, py);
            if (_mode == CaptureMode.Display) _displayMap?.UpdateMouse(px, py);
            UpdateVisuals();
            RaiseTarget();
            return;
        }

        if (_dragging)
        {
            if (_drawingNew) _model.DrawTo(px, py);
            else if (_activeHandle == SelectionHandle.Inside) _model.MoveBy(px - _lastPx, py - _lastPy);
            else _model.ResizeBy(_activeHandle, px - _lastPx, py - _lastPy);
            _lastPx = px; _lastPy = py;
            UpdateVisuals();
            RaiseTarget();
        }
        else
        {
            var handle = _model.HitTest(px, py, (int)(HandleTolerancePx * _scale));
            Cursor = CursorForHandle(handle);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false; _drawingNew = false;
        _mouseHeldHandle = SelectionHandle.None;
        ReleaseMouseCapture();
        UpdateVisuals();
        RaiseTarget();
    }

    // ---- keyboard ---------------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancelled?.Invoke();
                e.Handled = true;
                return;
            case Key.Enter:
                if (GetCurrentTarget() is not null) Confirmed?.Invoke();
                e.Handled = true;
                return;
        }

        if (_mode != CaptureMode.Custom || !_model.HasSelection)
            return;

        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -1; break;
            case Key.Right: dx = 1; break;
            case Key.Up: dy = -1; break;
            case Key.Down: dy = 1; break;
            default: return;
        }

        if (_mouseHeldHandle is not (SelectionHandle.None or SelectionHandle.Inside))
            _model.ResizeBy(_mouseHeldHandle, dx, dy);
        else
            _model.MoveBy(dx, dy);

        UpdateVisuals();
        RaiseTarget();
        e.Handled = true;
    }

    // ---- hover (display/window) ------------------------------------------

    private void UpdateHoverTarget(int px, int py)
    {
        if (_mode == CaptureMode.Display)
        {
            var mon = ScreenInfo.MonitorAt(px, py);
            _hoverRegion = mon.Bounds;
            _hoverWindow = nint.Zero;
            _hoverLabel = $"{mon.Bounds.Width}x{mon.Bounds.Height}" + (mon.IsPrimary ? " (primary)" : "");
        }
        else if (_mode == CaptureMode.Window)
        {
            var ignore = new[] { new WindowInteropHelper(this).Handle, ToolbarHandle };
            var hit = ScreenInfo.WindowAt(px, py, ignore);
            if (hit is { } h)
            {
                _hoverRegion = h.Bounds; _hoverWindow = h.Handle;
                _hoverLabel = $"{h.Bounds.Width}x{h.Bounds.Height}";
            }
            else
            {
                _hoverRegion = default; _hoverWindow = nint.Zero; _hoverLabel = null;
            }
        }
    }

    // ---- rendering --------------------------------------------------------

    private CaptureRegion CurrentRegionPhysical() => _mode switch
    {
        CaptureMode.Custom => _model?.Region ?? default,
        _ => _hoverRegion,
    };

    /// <summary>Mark visuals dirty; the actual work runs on the next render frame.</summary>
    private void UpdateVisuals() => _visualsDirty = true;

    private void UpdateVisualsCore()
    {
        var region = CurrentRegionPhysical();

        if (region.IsEmpty)
        {
            _dim?.ClearHole();
            SelectionBorder.Visibility = Visibility.Collapsed;
            InfoBadge.Visibility = Visibility.Collapsed;
            HideHandles();
            CenterHint();
            return;
        }

        HintBadge.Visibility = Visibility.Collapsed;

        _dim?.SetHole(region.X, region.Y, region.Width, region.Height);

        var holeRect = new Rect(
            PhysXToDip(region.X), PhysYToDip(region.Y),
            region.Width / _scale, region.Height / _scale);

        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, holeRect.X);
        Canvas.SetTop(SelectionBorder, holeRect.Y);
        SelectionBorder.Width = Math.Max(0, holeRect.Width);
        SelectionBorder.Height = Math.Max(0, holeRect.Height);

        if (_mode == CaptureMode.Custom) LayoutHandles(holeRect);
        else HideHandles();

        InfoText.Text = $"{region.Width} × {region.Height}";
        InfoBadge.Visibility = Visibility.Visible;
        InfoBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeY = holeRect.Y - InfoBadge.DesiredSize.Height - 6;
        if (badgeY < 0) badgeY = holeRect.Y + 6;
        Canvas.SetLeft(InfoBadge, holeRect.X);
        Canvas.SetTop(InfoBadge, badgeY);
    }

    private (double X, double Y, double W, double H) PrimaryMonitorDip()
    {
        var monitors = ScreenInfo.GetMonitors();
        var p = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return (PhysXToDip(p.Bounds.X), PhysYToDip(p.Bounds.Y), p.Bounds.Width / _scale, p.Bounds.Height / _scale);
    }

    private void CenterHint()
    {
        if (!_loaded) return;
        HintBadge.Visibility = Visibility.Visible;
        HintBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (px, py, pw, ph) = PrimaryMonitorDip();
        Canvas.SetLeft(HintBadge, px + (pw - HintBadge.DesiredSize.Width) / 2);
        Canvas.SetTop(HintBadge, py + ph * 0.45);
    }

    private void PositionToolbar()
    {
        if (!_loaded || _toolbarMoved) return;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : Toolbar.DesiredSize.Width;
        var (px, py, pw, _) = PrimaryMonitorDip();
        Canvas.SetLeft(Toolbar, px + (pw - width) / 2);
        Canvas.SetTop(Toolbar, py + 14);
    }

    private void RaiseTarget()
    {
        var target = GetCurrentTarget();
        RecordButton.IsEnabled = target is not null;
        TargetChanged?.Invoke(target);
    }

    // ---- handles ----------------------------------------------------------

    private void CreateHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            _handles.Add(new Rectangle
            {
                Width = 9, Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0x47, 0x47)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            });
        }
    }

    private void EnsureHandlesAttached()
    {
        foreach (var h in _handles)
            if (!RootCanvas.Children.Contains(h))
                RootCanvas.Children.Add(h);
    }

    private void HideHandles()
    {
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
    }

    private void LayoutHandles(Rect r)
    {
        EnsureHandlesAttached();
        var pts = new[]
        {
            new Point(r.Left, r.Top),
            new Point(r.Left + r.Width / 2, r.Top),
            new Point(r.Right, r.Top),
            new Point(r.Right, r.Top + r.Height / 2),
            new Point(r.Right, r.Bottom),
            new Point(r.Left + r.Width / 2, r.Bottom),
            new Point(r.Left, r.Bottom),
            new Point(r.Left, r.Top + r.Height / 2),
        };
        for (int i = 0; i < _handles.Count; i++)
        {
            var h = _handles[i];
            h.Visibility = Visibility.Visible;
            Canvas.SetLeft(h, pts[i].X - h.Width / 2);
            Canvas.SetTop(h, pts[i].Y - h.Height / 2);
        }
    }

    private static Cursor CursorForHandle(SelectionHandle handle) => handle switch
    {
        SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
        SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
        SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
        SelectionHandle.Inside => Cursors.SizeAll,
        _ => Cursors.Cross,
    };
}
