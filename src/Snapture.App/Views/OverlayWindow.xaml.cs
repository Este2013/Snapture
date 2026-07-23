using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private const int HandleTolerancePx = 10;

    private readonly List<Rectangle> _handles = new();
    private SelectionModel _model = null!;

    private int _vx, _vy, _vw, _vh;     // virtual desktop, physical px
    private double _scale = 1.0;         // physical px per DIP
    private CaptureRegion _homeMonitor;  // monitor under the cursor at open (chrome anchor)
    private bool _loaded;
    private bool _suppressModeEvents;

    private CaptureKind _kind;
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

    // Optional frozen-desktop backdrop (snapshot mode): shown below the dim so
    // transient popups captured at selection time stay visible and capturable.
    private readonly FrozenScreen? _frozen;
    private FreezeWindow? _freeze;

    // Picker toolbar anchor + keyboard/scroll controls (the overlay never takes
    // focus, so shortcuts arrive via a low-level hook).
    private readonly PickerBarPosition _barPosition;
    private readonly SelectionToolbarPlacement _selPlacement;
    private readonly IReadOnlyList<CaptureRegion> _history;
    private int _historyIndex = -1;
    private PickerInputHook? _hook;

    // Selection mini-toolbar state.
    private bool _aspectOn;
    private bool _rulerOn;
    private bool _selToolbarInside; // placed inside the selection → dim unless hovered
    private readonly System.Diagnostics.Stopwatch _cropCheckClock = System.Diagnostics.Stopwatch.StartNew();

    // Undo/redo of the selection rectangle (null = no selection).
    private readonly Stack<CaptureRegion?> _undo = new();
    private readonly Stack<CaptureRegion?> _redo = new();
    private CaptureRegion? _dragUndoState;

    // Display-mode picker: a Windows-Settings-style map of all monitors.
    private DisplayMapControl? _displayMap;
    private MonitorInfo? _mapHoveredMonitor; // display tile the pointer is over, if any

    // Logical-area snap (Custom mode, before a real selection exists): hovering
    // previews the tightest UI element under the cursor; the wheel walks up to its
    // parent; a plain click commits it; a drag falls back to a manual rectangle.
    private readonly DispatcherTimer _snapTimer;
    private IReadOnlyList<CaptureRegion> _snapChain = Array.Empty<CaptureRegion>();
    private int _snapIndex;
    private bool _probeInFlight;
    private int _wantProbePx, _wantProbePy;
    private bool _haveWantProbe;
    private int _lastProbePx = int.MinValue, _lastProbePy = int.MinValue;
    private static readonly DoubleCollection PreviewDash = new(new double[] { 4, 3 });

    // Press arbitration: in snap mode a press is deferred until we know whether
    // it's a click (commit the snap) or a drag (manual selection).
    private bool _pendingPress;
    private int _pressPx, _pressPy;

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

    public OverlayWindow(CaptureKind kind, CaptureMode mode, FrozenScreen? frozen = null,
        PickerBarPosition barPosition = PickerBarPosition.TopCenter,
        IReadOnlyList<CaptureRegion>? history = null,
        SelectionToolbarPlacement selToolbarPlacement = SelectionToolbarPlacement.Left)
    {
        InitializeComponent();
        _kind = kind;
        _mode = mode;
        _frozen = frozen;
        _barPosition = barPosition;
        _selPlacement = selToolbarPlacement;
        _history = history ?? Array.Empty<CaptureRegion>();
        // Never take foreground/activation: doing so dismisses transient popups
        // (menus, dropdowns) in the app being captured. We show no-activate and
        // route Enter/Esc via temporary global hotkeys instead of keyboard focus.
        ShowActivated = false;
        CreateHandles();

        WireToolbar(kind, mode);
        WireSelectionToolbar();

        Loaded += OnLoaded;
        SizeChanged += (_, _) => { UpdateVisuals(); PositionToolbar(); UpdateDisplayMap(); };
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;

        _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _snapTimer.Tick += OnSnapTick;
    }

    public nint ToolbarHandle { get; set; }

    /// <summary>Whether the user is set to take a video recording or a still snapshot.</summary>
    public CaptureKind Kind => _kind;

    /// <summary>The capture mode currently selected in the toolbar.</summary>
    public CaptureMode Mode => _mode;

    public event Action<CaptureTarget?>? TargetChanged;
    public event Action? Confirmed;
    public event Action? Cancelled;

    /// <summary>Raised when the user changes the capture mode mid-pick (Display/Window/Custom).</summary>
    public event Action? CaptureModeChanged;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    /// <summary>Keep clicks from activating (and thus dismissing the captured popup).</summary>
    private static nint NoActivateHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return nint.Zero;
    }

    /// <summary>Confirm the current target (from an Enter hotkey; there's no keyboard focus).</summary>
    public void TriggerConfirm()
    {
        if (GetCurrentTarget() is not null) Confirmed?.Invoke();
    }

    /// <summary>Cancel the pick (from an Esc hotkey; there's no keyboard focus).</summary>
    public void TriggerCancel() => Cancelled?.Invoke();

    private void WireToolbar(CaptureKind kind, CaptureMode mode)
    {
        _suppressModeEvents = true;
        KindSnapshot.IsChecked = kind == CaptureKind.Image;
        KindVideo.IsChecked = kind == CaptureKind.Video;
        KindText.IsChecked = kind == CaptureKind.Text;
        ModeDisplay.IsChecked = mode == CaptureMode.Display;
        ModeWindow.IsChecked = mode == CaptureMode.Window;
        ModeCustom.IsChecked = mode == CaptureMode.Custom;
        _suppressModeEvents = false;

        KindSnapshot.Checked += (_, _) => OnKindPicked(CaptureKind.Image);
        KindVideo.Checked += (_, _) => OnKindPicked(CaptureKind.Video);
        KindText.Checked += (_, _) => OnKindPicked(CaptureKind.Text);
        ModeDisplay.Checked += (_, _) => OnModePicked(CaptureMode.Display);
        ModeWindow.Checked += (_, _) => OnModePicked(CaptureMode.Window);
        ModeCustom.Checked += (_, _) => OnModePicked(CaptureMode.Custom);

        RecordButton.Click += (_, _) => { if (GetCurrentTarget() is not null) Confirmed?.Invoke(); };
        CancelButton.Click += (_, _) => Cancelled?.Invoke();
        Toolbar.SizeChanged += (_, _) => PositionToolbar();

        // Drag the toolbar by any empty area; child controls handle their own
        // clicks so this only fires on the toolbar background/padding.
        Toolbar.MouseLeftButtonDown += OnToolbarMouseDown;
        Toolbar.MouseMove += OnToolbarMouseMove;
        Toolbar.MouseLeftButtonUp += OnToolbarMouseUp;

        UpdateActionButton();
    }

    private void OnKindPicked(CaptureKind kind)
    {
        if (_suppressModeEvents) return;
        _kind = kind;            // capture mode stays sticky across a kind switch
        UpdateActionButton();
        RaiseTarget();
    }

    private void UpdateActionButton()
    {
        switch (_kind)
        {
            case CaptureKind.Image:
                RecordButton.Content = CaptureIcons.ScanCamera();
                RecordButton.ToolTip = "Take snapshot (Enter)";
                break;
            case CaptureKind.Text:
                RecordButton.Content = new TextBlock
                {
                    Text = ((char)0xE8C8).ToString(),
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16,
                };
                RecordButton.ToolTip = "Copy text (Enter)";
                break;
            default:
                RecordButton.Content = CaptureIcons.Record();
                RecordButton.ToolTip = "Record (Enter)";
                break;
        }
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
        UpdateVisuals();       // a docked selection toolbar follows the main one
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
        CaptureModeChanged?.Invoke(); // a mid-pick change overrides the saved default
    }

    public CaptureTarget? GetCurrentTarget()
    {
        switch (_mode)
        {
            case CaptureMode.Custom:
                if (_model is { HasSelection: true } && !_model.Region.ToEvenDimensions().IsEmpty)
                    return new CaptureTarget { Mode = CaptureMode.Custom, Region = _model.Region, Label = _model.Region.ToString() };
                // No committed selection yet: a snap preview is recordable directly.
                if (ShowingSnapPreview && !SnapRegion.ToEvenDimensions().IsEmpty)
                    return new CaptureTarget { Mode = CaptureMode.Custom, Region = SnapRegion, Label = SnapRegion.ToString() };
                return null;
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
        NativeMethods.MarkToolWindow(this, noActivate: true);

        // Reject mouse activation so clicking the overlay never steals foreground
        // from the app whose popup we're capturing.
        src?.AddHook(NoActivateHook);

        _model = new SelectionModel(new CaptureRegion(_vx, _vy, _vw, _vh));

        // Deliberately no Activate()/Focus(): keeping the source app in the
        // foreground is what lets its live popup stay open during the pick.

        if (NativeMethods.GetCursorPos(out var p))
        {
            _cursorPx = p.X; _cursorPy = p.Y;
            if (_mode != CaptureMode.Custom)
                UpdateHoverTarget(_cursorPx, _cursorPy);
        }

        // Anchor the toolbar/picker on the monitor the cursor is on right now.
        // Captured once: the chrome doesn't chase the cursor across monitors.
        _homeMonitor = ScreenInfo.MonitorAt(_cursorPx, _cursorPy).Bounds;

        _loaded = true;

        // Dim layer behind us. ShowActivated=false keeps our keyboard focus; we
        // then drop it directly below this window so our chrome stays on top.
        _dim = new DimWindow(_vx, _vy, _vw, _vh, DimAlpha);
        _dim.Show();
        NativeMethods.PlaceDirectlyBelow(_dim, this);
        _dim.ClearHole();

        // Frozen-desktop backdrop, one layer further down: the dim shades it and
        // the selection hole reveals it, so the pick behaves as usual while the
        // frozen popups remain visible (and get captured from these pixels).
        if (_frozen is not null)
        {
            _freeze = new FreezeWindow(_frozen);
            _freeze.Show();
            NativeMethods.PlaceDirectlyBelow(_freeze, _dim);
        }

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
        _displayMap.DisplayHovered += OnDisplayHovered;
        RootCanvas.Children.Insert(0, _displayMap);
        _displayMap.Build(ScreenInfo.GetMonitors());

        UpdateVisualsCore();
        PositionToolbar();
        UpdateDisplayMap();
        RaiseTarget(); // set the action button's initial enabled state
        _snapTimer.Start();

        // The overlay never takes focus, so keyboard shortcuts + wheel come via a
        // low-level hook (and are swallowed so they don't reach the app underneath).
        _hook = new PickerInputHook(Dispatcher,
            onEnter: TriggerConfirm, onEsc: TriggerCancel,
            onRetake: HistoryOlder, onHistoryBack: HistoryNewer,
            onUndo: Undo, onRedo: Redo, onWheel: HandleWheel, onArrow: Nudge);
    }

    private void OnDisplayPicked(MonitorInfo m)
    {
        _hoverRegion = m.Bounds;
        _hoverWindow = nint.Zero;
        _hoverLabel = $"{m.Bounds.Width}x{m.Bounds.Height}" + (m.IsPrimary ? " (primary)" : "");
        Confirmed?.Invoke();
    }

    /// <summary>Hovering a display tile previews that monitor (its real screen gets the selection).</summary>
    private void OnDisplayHovered(MonitorInfo? m)
    {
        _mapHoveredMonitor = m;
        if (m is not null)
        {
            _hoverRegion = m.Bounds;
            _hoverWindow = nint.Zero;
            _hoverLabel = $"{m.Bounds.Width}x{m.Bounds.Height}" + (m.IsPrimary ? " (primary)" : "");
        }
        else
        {
            UpdateHoverTarget(_cursorPx, _cursorPy);
        }
        _displayMap?.Highlight(_hoverRegion);
        UpdateVisuals();
        RaiseTarget();
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
        var (px, py, pw, ph) = ActiveMonitorDip();
        Canvas.SetLeft(_displayMap, px + (pw - _displayMap.DesiredSize.Width) / 2);
        Canvas.SetTop(_displayMap, py + (ph - _displayMap.DesiredSize.Height) / 2);
        _displayMap.UpdateMouse(_cursorPx, _cursorPy);
        _displayMap.Highlight(_hoverRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _snapTimer.Stop();
        var dim = _dim; _dim = null;
        try { dim?.Close(); } catch { }
        var freeze = _freeze; _freeze = null;
        try { freeze?.Close(); } catch { }
        var hook = _hook; _hook = null;
        try { hook?.Dispose(); } catch { }
    }

    // ---- logical-area snap (Custom mode) ---------------------------------

    /// <summary>True while we should be previewing/probing logical areas.</summary>
    private bool SnapEligible =>
        _mode == CaptureMode.Custom && _model is { HasSelection: false } && !_dragging && !_pendingPress;

    private bool ShowingSnapPreview =>
        _mode == CaptureMode.Custom && (_model?.HasSelection == false) && _snapChain.Count > 0;

    private CaptureRegion SnapRegion => _snapChain[Math.Clamp(_snapIndex, 0, _snapChain.Count - 1)];

    private void OnSnapTick(object? sender, EventArgs e)
    {
        if (!SnapEligible || _probeInFlight || !_haveWantProbe) return;
        if (_wantProbePx == _lastProbePx && _wantProbePy == _lastProbePy) return;

        int px = _wantProbePx, py = _wantProbePy;
        _lastProbePx = px; _lastProbePy = py;
        _probeInFlight = true;
        var own = new WindowInteropHelper(this).Handle;

        Task.Run(() => LogicalAreaProbe.AreasAt(px, py, own, ToolbarHandle))
            .ContinueWith(t =>
            {
                _probeInFlight = false;
                if (SnapEligible) ApplyChain(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyChain(IReadOnlyList<CaptureRegion> chain)
    {
        // Keep the user's scrolled depth only if we're still over the same element.
        bool sameDeepest = _snapChain.Count > 0 && chain.Count > 0
            && LogicalAreaProbe.NearEqual(_snapChain[0], chain[0]);

        _snapChain = chain;
        if (!sameDeepest) _snapIndex = 0;
        _snapIndex = Math.Clamp(_snapIndex, 0, Math.Max(0, chain.Count - 1));

        UpdateVisuals();
        RaiseTarget();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleWheel(e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Wheel behaviour: before a selection exists, walk the snap tree (parent/child);
    /// once a custom rectangle is committed, grow/shrink it from its centre.
    /// </summary>
    public void HandleWheel(int delta)
    {
        if (_mode == CaptureMode.Custom && (_model?.HasSelection ?? false))
        {
            int step = Math.Max(2, (int)Math.Round(8 * _scale));
            RecordUndo(CurrentState());
            _model!.Inflate(delta > 0 ? step : -step);
            UpdateVisuals();
            RaiseTarget();
            return;
        }

        if (!SnapEligible || _snapChain.Count == 0) return;
        // Wheel up → parent (larger area); wheel down → child (tighter area).
        _snapIndex = delta > 0
            ? Math.Min(_snapIndex + 1, _snapChain.Count - 1)
            : Math.Max(_snapIndex - 1, 0);
        UpdateVisuals();
        RaiseTarget();
    }

    // ---- undo / redo / history ------------------------------------------

    private CaptureRegion? CurrentState() => (_model?.HasSelection ?? false) ? _model!.Region : null;

    private void RecordUndo(CaptureRegion? before)
    {
        _undo.Push(before);
        _redo.Clear();
    }

    private void RecordUndoIfChanged()
    {
        var now = CurrentState();
        if (!Nullable.Equals(now, _dragUndoState))
            RecordUndo(_dragUndoState);
        _dragUndoState = now;
    }

    private void ApplyState(CaptureRegion? state)
    {
        EnsureCustomMode();
        if (state is { } r) _model.Set(r); else _model.Clear();
        _snapChain = Array.Empty<CaptureRegion>();
        UpdateVisuals();
        RaiseTarget();
    }

    /// <summary>Undo the last selection change.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CurrentState());
        ApplyState(_undo.Pop());
    }

    /// <summary>Redo the last undone selection change.</summary>
    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CurrentState());
        ApplyState(_redo.Pop());
    }

    /// <summary>R: step back in time — first press loads the most recent, then older.</summary>
    public void HistoryOlder() =>
        LoadHistory(_historyIndex < 0 ? 0 : _historyIndex + 1);

    /// <summary>Shift+R: step forward in time (towards the most recent position).</summary>
    public void HistoryNewer() =>
        LoadHistory(_historyIndex < 0 ? 0 : _historyIndex - 1);

    private void LoadHistory(int index)
    {
        if (_history.Count == 0) return;
        index = Math.Clamp(index, 0, _history.Count - 1);
        if (index == _historyIndex) return; // already showing this position
        _historyIndex = index;
        RecordUndo(CurrentState());
        EnsureCustomMode();
        _model.Set(_history[index]);
        _snapChain = Array.Empty<CaptureRegion>();
        _dragUndoState = CurrentState();
        UpdateVisuals();
        RaiseTarget();
    }

    private void EnsureCustomMode()
    {
        if (_mode == CaptureMode.Custom) return;
        _suppressModeEvents = true;
        ModeCustom.IsChecked = true;
        _suppressModeEvents = false;
        _mode = CaptureMode.Custom;
        UpdateDisplayMap();
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
        _dragUndoState = CurrentState(); // snapshot before this interaction changes it
        HideCropPicker();

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
            CaptureMouse();
            UpdateVisuals();
            RaiseTarget();
            return;
        }

        if (!_model.HasSelection)
        {
            // Snap mode: defer until move/up decides click (commit) vs drag (draw).
            _pendingPress = true; _pressPx = px; _pressPy = py;
            CaptureMouse();
            return;
        }

        // A selection exists but the press was outside it → start a fresh draw.
        _model.BeginDraw(px, py);
        _drawingNew = true; _dragging = true;
        _activeHandle = SelectionHandle.BottomRight;
        _mouseHeldHandle = SelectionHandle.None;
        HintBadge.Visibility = Visibility.Collapsed;
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
            // A hovered display tile takes priority over the monitor the physical
            // cursor happens to be on (which is where the map itself lives).
            if (_mode == CaptureMode.Display && _mapHoveredMonitor is { } hm)
            {
                _hoverRegion = hm.Bounds;
                _hoverWindow = nint.Zero;
                _hoverLabel = $"{hm.Bounds.Width}x{hm.Bounds.Height}" + (hm.IsPrimary ? " (primary)" : "");
            }
            else
            {
                UpdateHoverTarget(px, py);
            }
            if (_mode == CaptureMode.Display)
            {
                _displayMap?.UpdateMouse(px, py);
                _displayMap?.Highlight(_hoverRegion);
            }
            UpdateVisuals();
            RaiseTarget();
            return;
        }

        if (_pendingPress)
        {
            int threshold = Math.Max(3, (int)(4 * _scale));
            if (Math.Abs(px - _pressPx) > threshold || Math.Abs(py - _pressPy) > threshold)
            {
                // The press turned into a drag → manual rectangle, ignore the snap.
                _pendingPress = false;
                _snapChain = Array.Empty<CaptureRegion>();
                _model.BeginDraw(_pressPx, _pressPy);
                _drawingNew = true; _dragging = true;
                _activeHandle = SelectionHandle.BottomRight;
                _mouseHeldHandle = SelectionHandle.None;
                HintBadge.Visibility = Visibility.Collapsed;
                _model.DrawTo(px, py);
                UpdateVisuals();
                RaiseTarget();
            }
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
        else if (_model.HasSelection)
        {
            var handle = _model.HitTest(px, py, (int)(HandleTolerancePx * _scale));
            Cursor = CursorForHandle(handle);
        }
        else
        {
            // Snap mode: ask the background probe for the area under the cursor.
            _wantProbePx = px; _wantProbePy = py; _haveWantProbe = true;
            Cursor = Cursors.Cross;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingPress)
        {
            // A plain click in snap mode → commit the previewed area as the real,
            // now-resizable selection.
            _pendingPress = false;
            ReleaseMouseCapture();
            if (_snapChain.Count > 0)
            {
                _model.Set(SnapRegion);
                _snapChain = Array.Empty<CaptureRegion>();
                _mouseHeldHandle = SelectionHandle.None;
            }
            RecordUndoIfChanged();
            UpdateVisuals();
            RaiseTarget();
            return;
        }

        _dragging = false; _drawingNew = false;
        _mouseHeldHandle = SelectionHandle.None;
        ReleaseMouseCapture();
        RecordUndoIfChanged();
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

        switch (e.Key)
        {
            case Key.Left: Nudge(-1, 0); e.Handled = true; break;
            case Key.Right: Nudge(1, 0); e.Handled = true; break;
            case Key.Up: Nudge(0, -1); e.Handled = true; break;
            case Key.Down: Nudge(0, 1); e.Handled = true; break;
        }
    }

    /// <summary>Nudge (move) or, while a handle is held, resize the selection by a pixel delta.</summary>
    public void Nudge(int dx, int dy)
    {
        if (_mode != CaptureMode.Custom || !(_model?.HasSelection ?? false))
            return;
        if (_mouseHeldHandle is not (SelectionHandle.None or SelectionHandle.Inside))
            _model.ResizeBy(_mouseHeldHandle, dx, dy);
        else
            _model.MoveBy(dx, dy);
        UpdateVisuals();
        RaiseTarget();
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

    // ---- selection mini-toolbar ------------------------------------------

    private void WireSelectionToolbar()
    {
        CropDisplayBtn.Click += (_, _) => CropToDisplay();
        CropWindowBtn.Click += (_, _) => CropToWindow();
        AspectBtn.Click += (_, _) => ToggleAspect();
        RulerBtn.Click += (_, _) => ToggleRuler();

        // Don't let clicks on the bars start a new selection.
        SelectionToolbar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        CropPicker.MouseLeftButtonDown += (_, e) => e.Handled = true;

        // When placed inside the selection, dim until hovered.
        SelectionToolbar.MouseEnter += (_, _) => { if (_selToolbarInside) SelectionToolbar.Opacity = 1.0; };
        SelectionToolbar.MouseLeave += (_, _) => { if (_selToolbarInside) SelectionToolbar.Opacity = 0.5; };
    }

    /// <summary>Show/place the mini-toolbar for a committed custom selection; hide otherwise.</summary>
    private void UpdateSelectionToolbar()
    {
        bool show = _mode == CaptureMode.Custom && (_model?.HasSelection ?? false);
        if (!show)
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            HideCropPicker();
            return;
        }

        // Vertical bar when attached to a side (or docked beside a vertical main bar).
        bool vertical = _selPlacement is SelectionToolbarPlacement.Left or SelectionToolbarPlacement.Right
            || (_selPlacement == SelectionToolbarPlacement.DockToMain && _barPosition is PickerBarPosition.LeftCenter or PickerBarPosition.RightCenter);
        SelectionToolStack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        var toolPad = vertical ? new Thickness(9, 11, 9, 11) : new Thickness(9, 7, 9, 7);
        foreach (var b in new[] { CropDisplayBtn, CropWindowBtn, AspectBtn, RulerBtn }) b.Padding = toolPad;

        SelectionToolbar.Visibility = Visibility.Visible;
        SelectionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = SelectionToolbar.DesiredSize.Width, th = SelectionToolbar.DesiredSize.Height;

        // Crop buttons are only usable when there's something to crop to. This
        // enumerates windows (with occlusion sampling), so skip it during a drag
        // and throttle it otherwise.
        if (!_dragging && _cropCheckClock.ElapsedMilliseconds > 200)
        {
            _cropCheckClock.Restart();
            CropDisplayBtn.IsEnabled = DisplayCropCandidates().Count > 0;
            CropWindowBtn.IsEnabled = WindowCropCandidates().Count > 0;
        }

        var r = _model!.Region;
        var mon = MonitorForRegion(r);
        double sx = PhysXToDip(r.X), sy = PhysYToDip(r.Y), sw = r.Width / _scale, sh = r.Height / _scale;
        double mx = PhysXToDip(mon.X), my = PhysYToDip(mon.Y), mw = mon.Width / _scale, mh = mon.Height / _scale;
        double sR = sx + sw, sB = sy + sh, mR = mx + mw, mB = my + mh;
        const double gap = 8;

        double x = sx, y = sy;
        _selToolbarInside = false;

        switch (_selPlacement)
        {
            case SelectionToolbarPlacement.DockToMain:
                (x, y) = DockNextToMainBar(tw, th, gap);
                break;
            case SelectionToolbarPlacement.Left:
                if (sx - mx >= tw + gap) x = sx - gap - tw;
                else { _selToolbarInside = true; x = sx + gap; }
                y = Center(sy, sh, th, my, mh);
                break;
            case SelectionToolbarPlacement.Right:
                if (mR - sR >= tw + gap) x = sR + gap;
                else { _selToolbarInside = true; x = sR - gap - tw; }
                y = Center(sy, sh, th, my, mh);
                break;
            case SelectionToolbarPlacement.Top:
                if (sy - my >= th + gap) y = sy - gap - th;
                else { _selToolbarInside = true; y = sy + gap; }
                x = Center(sx, sw, tw, mx, mw);
                break;
            case SelectionToolbarPlacement.Bottom:
                if (mB - sB >= th + gap) y = sB + gap;
                else { _selToolbarInside = true; y = sB - gap - th; }
                x = Center(sx, sw, tw, mx, mw);
                break;
        }

        // Never let the bar spill off the selection's monitor.
        x = Clamp(x, mx, mx + mw - tw);
        y = Clamp(y, my, my + mh - th);

        Canvas.SetLeft(SelectionToolbar, x);
        Canvas.SetTop(SelectionToolbar, y);
        SelectionToolbar.Opacity = _selToolbarInside && !SelectionToolbar.IsMouseOver ? 0.5 : 1.0;

        if (CropPicker.Visibility == Visibility.Visible) PositionCropPicker();
    }

    /// <summary>Centre the bar on the selection edge, clamped to the monitor.</summary>
    private static double Center(double selStart, double selLen, double barLen, double monStart, double monLen) =>
        Clamp(selStart + (selLen - barLen) / 2, monStart, monStart + monLen - barLen);

    private static double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);

    private (double x, double y) DockNextToMainBar(double tw, double th, double gap)
    {
        double ml = Canvas.GetLeft(Toolbar), mt = Canvas.GetTop(Toolbar);
        double mwid = Toolbar.ActualWidth, mhei = Toolbar.ActualHeight;
        var (amx, amy, amw, amh) = ActiveMonitorDip();
        double x, y;
        switch (_barPosition)
        {
            case PickerBarPosition.LeftCenter: x = ml + mwid + gap; y = mt + (mhei - th) / 2; break;
            case PickerBarPosition.RightCenter: x = ml - gap - tw; y = mt + (mhei - th) / 2; break;
            case PickerBarPosition.BottomLeft or PickerBarPosition.BottomCenter or PickerBarPosition.BottomRight:
                x = ml + (mwid - tw) / 2; y = mt - gap - th; break;
            default: x = ml + (mwid - tw) / 2; y = mt + mhei + gap; break; // top-anchored → below
        }
        return (Clamp(x, amx, amx + amw - tw), Clamp(y, amy, amy + amh - th));
    }

    private void ToggleAspect()
    {
        _aspectOn = !_aspectOn;
        _model?.SetAspectLock(_aspectOn);
        AspectBtn.Content = _aspectOn ? ((char)0xE72E).ToString() : ((char)0xE785).ToString(); // locked / unlocked padlock
        AspectBtn.Background = _aspectOn ? OnBrush : System.Windows.Media.Brushes.Transparent;
    }

    private void ToggleRuler()
    {
        _rulerOn = !_rulerOn;
        RulerBtn.Background = _rulerOn ? OnBrush : System.Windows.Media.Brushes.Transparent;
        UpdateVisuals();
    }

    private static readonly System.Windows.Media.Brush OnBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    // ---- crop to display / window ----------------------------------------

    private readonly record struct CropOption(string Label, CaptureRegion Bounds, int Number, System.Windows.Media.ImageSource? Icon);

    private void CropToDisplay() => RunCrop(DisplayCropCandidates());
    private void CropToWindow() => RunCrop(WindowCropCandidates());

    private void RunCrop(List<CropOption> hits)
    {
        if (hits.Count == 0) return;
        if (hits.Count == 1) { CropTo(hits[0].Bounds); return; }
        ShowCropPicker(hits);
    }

    /// <summary>Displays that overlap the selection but don't already fully contain it.</summary>
    private List<CropOption> DisplayCropCandidates()
    {
        var list = new List<CropOption>();
        if (!(_model?.HasSelection ?? false)) return list;
        var sel = _model.Region;
        var monitors = ScreenInfo.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (Intersects(m.Bounds, sel) && !Contains(m.Bounds, sel))
                list.Add(new CropOption(DisplayName(m), m.Bounds, i + 1, null));
        }
        return list;
    }

    private static string DisplayName(MonitorInfo m)
    {
        var name = m.Name.TrimStart('\\', '.');
        return m.IsPrimary ? $"{name} (primary)" : name;
    }

    /// <summary>Windows overlapping the selection, visible there (not fully covered), not already containing it.</summary>
    private List<CropOption> WindowCropCandidates()
    {
        var list = new List<CropOption>();
        if (!(_model?.HasSelection ?? false)) return list;
        var sel = _model.Region;
        var own = new WindowInteropHelper(this).Handle;
        foreach (var w in ScreenInfo.GetOpenWindows())
        {
            if (w.Handle == own || w.Handle == ToolbarHandle) continue;
            if (!Intersects(w.Bounds, sel) || Contains(w.Bounds, sel)) continue;
            if (!VisibleInSelection(w.Handle, w.Bounds, sel)) continue; // fully covered by others
            list.Add(new CropOption(Trim(w.Title), w.Bounds, 0, WindowIcon.For(w.Handle)));
        }
        return list;
    }

    private static bool Contains(CaptureRegion outer, CaptureRegion inner) =>
        outer.X <= inner.X && outer.Y <= inner.Y && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    /// <summary>True if the window is topmost at any sampled point of its overlap (i.e. not fully covered).</summary>
    private static bool VisibleInSelection(nint handle, CaptureRegion bounds, CaptureRegion sel)
    {
        var i = Intersect(bounds, sel);
        if (i.IsEmpty) return false;
        for (int gx = 1; gx <= 3; gx++)
            for (int gy = 1; gy <= 3; gy++)
            {
                var hit = ScreenInfo.WindowAt(i.X + i.Width * gx / 4, i.Y + i.Height * gy / 4, Array.Empty<nint>());
                if (hit?.Handle == handle) return true;
            }
        return false;
    }

    private void CropTo(CaptureRegion region)
    {
        if (!(_model?.HasSelection ?? false)) return;
        var cropped = Intersect(_model.Region, region);
        if (cropped.ToEvenDimensions().IsEmpty) return;
        RecordUndo(CurrentState());
        _model.Set(cropped); // Set() refreshes the aspect-lock ratio to the new shape
        _dragUndoState = CurrentState();
        HideCropPreview();
        HideCropPicker();
        UpdateVisuals();
        RaiseTarget();
    }

    private void ShowCropPicker(IEnumerable<CropOption> items)
    {
        CropPickerList.Children.Clear();
        foreach (var opt in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (opt.Number > 0)
            {
                // Display: a numbered box, then the display's name.
                row.Children.Add(new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = opt.Number.ToString(), FontWeight = FontWeights.SemiBold },
                });
            }
            else if (opt.Icon is not null)
            {
                // Window: its icon, then the title.
                row.Children.Add(new System.Windows.Controls.Image { Source = opt.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            row.Children.Add(new TextBlock { Text = opt.Label, VerticalAlignment = VerticalAlignment.Center });

            var bounds = opt.Bounds;
            var b = new Button { Content = row, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 1, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            b.MouseEnter += (_, _) => ShowCropPreview(bounds);   // preview the result
            b.MouseLeave += (_, _) => HideCropPreview();
            b.Click += (_, _) => CropTo(bounds);
            CropPickerList.Children.Add(b);
        }
        CropPicker.Visibility = Visibility.Visible;
        PositionCropPicker();
    }

    /// <summary>Show a dashed preview of what the selection becomes if this option is chosen.</summary>
    private void ShowCropPreview(CaptureRegion region)
    {
        if (!(_model?.HasSelection ?? false)) return;
        var c = Intersect(_model.Region, region);
        if (c.IsEmpty) { HideCropPreview(); return; }
        Canvas.SetLeft(CropPreview, PhysXToDip(c.X));
        Canvas.SetTop(CropPreview, PhysYToDip(c.Y));
        CropPreview.Width = c.Width / _scale;
        CropPreview.Height = c.Height / _scale;
        CropPreview.Visibility = Visibility.Visible;
    }

    private void HideCropPreview() => CropPreview.Visibility = Visibility.Collapsed;

    private void PositionCropPicker()
    {
        CropPicker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = Canvas.GetLeft(SelectionToolbar);
        double y = Canvas.GetTop(SelectionToolbar) + SelectionToolbar.ActualHeight + 4;
        var (amx, amy, amw, amh) = ActiveMonitorDip();
        Canvas.SetLeft(CropPicker, Clamp(x, amx, amx + amw - CropPicker.DesiredSize.Width));
        Canvas.SetTop(CropPicker, Clamp(y, amy, amy + amh - CropPicker.DesiredSize.Height));
    }

    private void HideCropPicker() { CropPicker.Visibility = Visibility.Collapsed; HideCropPreview(); }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..39] + "…";

    private static bool Intersects(CaptureRegion a, CaptureRegion b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static CaptureRegion Intersect(CaptureRegion a, CaptureRegion b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.Right, b.Right), bo = Math.Min(a.Bottom, b.Bottom);
        return new CaptureRegion(l, t, Math.Max(0, r - l), Math.Max(0, bo - t));
    }

    private CaptureRegion MonitorForRegion(CaptureRegion r)
    {
        CaptureRegion best = default;
        long bestOverlap = -1;
        foreach (var m in ScreenInfo.GetMonitors())
        {
            var i = Intersect(m.Bounds, r);
            long area = (long)i.Width * i.Height;
            if (area > bestOverlap) { bestOverlap = area; best = m.Bounds; }
        }
        return best.IsEmpty ? ScreenInfo.MonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2).Bounds : best;
    }

    // ---- ruler -----------------------------------------------------------

    private void DrawRuler(CaptureRegion r)
    {
        RulerLayer.Children.Clear();
        if (!_rulerOn || r.IsEmpty) return;

        var mon = MonitorForRegion(r);
        double sx = PhysXToDip(r.X), sy = PhysYToDip(r.Y), sw = r.Width / _scale, sh = r.Height / _scale;
        double mx = PhysXToDip(mon.X), my = PhysYToDip(mon.Y), mw = mon.Width / _scale, mh = mon.Height / _scale;
        double sR = sx + sw, sB = sy + sh, mR = mx + mw, mB = my + mh;

        int gapL = r.X - mon.X, gapR = mon.Right - r.Right, gapT = r.Y - mon.Y, gapB = mon.Bottom - r.Bottom;

        // Projection lines from all four corners to the screen edges.
        RulerLine(sx, sy, sx, my); RulerLine(sR, sy, sR, my); // top
        RulerLine(sx, sB, sx, mB); RulerLine(sR, sB, sR, mB); // bottom
        RulerLine(sx, sy, mx, sy); RulerLine(sx, sB, mx, sB); // left
        RulerLine(sR, sy, mR, sy); RulerLine(sR, sB, mR, sB); // right

        // Segment lengths at the screen edges, near each touchdown (all four corners).
        RulerLabel($"{gapL}", (mx + sx) / 2, my + 9); RulerLabel($"{gapR}", (sR + mR) / 2, my + 9); // top edge
        RulerLabel($"{gapL}", (mx + sx) / 2, mB - 9); RulerLabel($"{gapR}", (sR + mR) / 2, mB - 9); // bottom edge
        RulerLabel($"{gapT}", mx + 14, (my + sy) / 2); RulerLabel($"{gapB}", mx + 14, (sB + mB) / 2); // left edge
        RulerLabel($"{gapT}", mR - 14, (my + sy) / 2); RulerLabel($"{gapB}", mR - 14, (sB + mB) / 2); // right edge
    }

    private void RulerLine(double x1, double y1, double x2, double y2)
    {
        RulerLayer.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = SelectionBorder.Stroke,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection(new double[] { 3, 3 }),
            IsHitTestVisible = false,
        });
    }

    private void RulerLabel(string text, double x, double y)
    {
        var tb = new TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.White, FontSize = 11, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        var badge = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0, 0, 0)),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Child = tb,
            IsHitTestVisible = false,
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(badge, x - badge.DesiredSize.Width / 2);
        Canvas.SetTop(badge, y - badge.DesiredSize.Height / 2);
        RulerLayer.Children.Add(badge);
    }

    // ---- rendering --------------------------------------------------------

    private CaptureRegion CurrentRegionPhysical() => _mode switch
    {
        CaptureMode.Custom => (_model?.HasSelection ?? false) ? _model!.Region
            : (_snapChain.Count > 0 ? SnapRegion : default),
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
            UpdateSelectionToolbar();
            DrawRuler(default);
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

        // A committed custom selection is resizable (handles, solid border); a
        // snap preview is a plain dashed rectangle with no handles.
        HideHandles();
        SelectionBorder.StrokeDashArray = null;
        if (_mode == CaptureMode.Custom)
        {
            if (_model?.HasSelection ?? false)
                LayoutHandles(holeRect);
            else
                SelectionBorder.StrokeDashArray = PreviewDash;
        }

        InfoText.Text = $"{region.Width} × {region.Height}";
        InfoBadge.Visibility = Visibility.Visible;
        InfoBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Inside the selection (top-left) so it's never covered or pushed off-screen.
        Canvas.SetLeft(InfoBadge, holeRect.X + 6);
        Canvas.SetTop(InfoBadge, holeRect.Y + 6);

        UpdateSelectionToolbar();
        DrawRuler(_mode == CaptureMode.Custom && (_model?.HasSelection ?? false) ? _model!.Region : default);
    }

    private (double X, double Y, double W, double H) PrimaryMonitorDip()
    {
        var monitors = ScreenInfo.GetMonitors();
        var p = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return (PhysXToDip(p.Bounds.X), PhysYToDip(p.Bounds.Y), p.Bounds.Width / _scale, p.Bounds.Height / _scale);
    }

    /// <summary>DIP rect of the monitor the chrome is anchored to (where the cursor opened).</summary>
    private (double X, double Y, double W, double H) ActiveMonitorDip()
    {
        if (_homeMonitor.IsEmpty) return PrimaryMonitorDip();
        return (PhysXToDip(_homeMonitor.X), PhysYToDip(_homeMonitor.Y),
                _homeMonitor.Width / _scale, _homeMonitor.Height / _scale);
    }

    private void CenterHint()
    {
        if (!_loaded) return;
        HintBadge.Visibility = Visibility.Visible;
        HintBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (px, py, pw, ph) = ActiveMonitorDip();
        Canvas.SetLeft(HintBadge, px + (pw - HintBadge.DesiredSize.Width) / 2);
        Canvas.SetTop(HintBadge, py + ph * 0.45);
    }

    private void PositionToolbar()
    {
        if (!_loaded || _toolbarMoved) return;

        bool vertical = _barPosition is PickerBarPosition.LeftCenter or PickerBarPosition.RightCenter;
        ApplyToolbarOrientation(vertical);

        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Toolbar.DesiredSize.Width, h = Toolbar.DesiredSize.Height;
        var (px, py, pw, ph) = ActiveMonitorDip();
        const double m = 14;

        double left = _barPosition switch
        {
            PickerBarPosition.TopLeft or PickerBarPosition.BottomLeft or PickerBarPosition.LeftCenter => px + m,
            PickerBarPosition.TopRight or PickerBarPosition.BottomRight or PickerBarPosition.RightCenter => px + pw - w - m,
            _ => px + (pw - w) / 2, // top/bottom centre
        };
        double top = _barPosition switch
        {
            PickerBarPosition.TopLeft or PickerBarPosition.TopCenter or PickerBarPosition.TopRight => py + m,
            PickerBarPosition.BottomLeft or PickerBarPosition.BottomCenter or PickerBarPosition.BottomRight => py + ph - h - m,
            _ => py + (ph - h) / 2, // left/right centre
        };

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    /// <summary>Lay the toolbar out horizontally or vertically and re-space its groups.</summary>
    private void ApplyToolbarOrientation(bool vertical)
    {
        var target = vertical ? Orientation.Vertical : Orientation.Horizontal;
        if (ToolbarStack.Orientation == target && _toolbarOrientationApplied) return;
        _toolbarOrientationApplied = true;
        ToolbarStack.Orientation = target;

        // Segmented groups stack their buttons along the bar's long axis.
        KindGrid.Rows = vertical ? 3 : 1; KindGrid.Columns = vertical ? 1 : 3;
        ModeGrid.Rows = vertical ? 3 : 1; ModeGrid.Columns = vertical ? 1 : 3;

        // Vertical uses uniform icon buttons; horizontal keeps the text labels.
        var iconVis = vertical ? Visibility.Visible : Visibility.Collapsed;
        var textVis = vertical ? Visibility.Collapsed : Visibility.Visible;
        ModeDisplayIcon.Visibility = ModeWindowIcon.Visibility = ModeCustomIcon.Visibility = iconVis;
        ModeDisplayText.Visibility = ModeWindowText.Visibility = ModeCustomText.Visibility = textVis;
        foreach (var b in new[] { ModeDisplay, ModeWindow, ModeCustom })
            b.MinWidth = vertical ? 46 : 78;

        // Give the icon buttons room to breathe when stacked vertically.
        foreach (var b in new[] { KindSnapshot, KindVideo, ModeDisplay, ModeWindow, ModeCustom })
            b.Padding = vertical ? new Thickness(14, 12, 14, 12) : new Thickness(14, 6, 14, 6);
        RecordButton.Padding = vertical ? new Thickness(12, 12, 12, 12) : new Thickness(12, 8, 12, 8);
        CancelButton.Padding = vertical ? new Thickness(11, 12, 11, 12) : new Thickness(11, 8, 11, 8);

        // Rotate the drag dots so the handle still reads across a vertical bar.
        DragHandle.LayoutTransform = vertical ? new RotateTransform(90) : null;

        // Stack along the new axis; when vertical, make every group the same width.
        for (int i = 0; i < ToolbarStack.Children.Count; i++)
        {
            if (ToolbarStack.Children[i] is not FrameworkElement el) continue;
            el.Margin = i == 0 ? new Thickness(0)
                : vertical ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
            el.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }
        DragHandle.HorizontalAlignment = HorizontalAlignment.Center; // the handle itself stays centred

        // Tooltips: instant, and anchored to the outward side of the bar at the
        // hovered button's height (bar on the left → tooltips on the right).
        var placement = _barPosition == PickerBarPosition.LeftCenter ? PlacementMode.Right : PlacementMode.Left;
        foreach (FrameworkElement el in new FrameworkElement[] { KindSnapshot, KindVideo, ModeDisplay, ModeWindow, ModeCustom, RecordButton, CancelButton })
        {
            ToolTipService.SetInitialShowDelay(el, vertical ? 0 : 400);
            ToolTipService.SetPlacement(el, vertical ? placement : PlacementMode.Mouse);
        }
    }

    private bool _toolbarOrientationApplied;

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
