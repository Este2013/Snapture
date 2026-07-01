using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Snapture.App.Interop;
using Snapture.App.Tray;
using Snapture.App.Views;
using Snapture.Core.Ipc;
using Snapture.Core.Models;
using Snapture.Core.Recording;
using Snapture.Core.Settings;
using Snapture.Core.Snapshot;

namespace Snapture.App;

/// <summary>
/// Application-level coordinator: owns the tray icon, the settings window, the
/// transient capture overlay and recording bar, the <see cref="RecordingController"/>,
/// and the Stream Deck <see cref="ControlServer"/>. All recording — from the
/// tray, the settings window, or the IPC socket — funnels through here.
/// </summary>
public sealed class AppController : IControlCommandHandler, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settings;
    private readonly RecordingController _controller;
    private readonly SnapshotService _snapshot;
    private ControlServer? _server;
    private HotkeyService? _hotkeys;

    private TaskbarIcon? _tray;
    private MainWindow? _mainWindow;
    private OverlayWindow? _overlay;
    private RecordingBarWindow? _recordingBar;

    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();

    // Single/double tray-click disambiguation.
    private readonly DispatcherTimer _clickTimer;
    private bool _clickPending;

    private string? _lastSavedPath;

    public AppController()
    {
        _dispatcher = Application.Current.Dispatcher;
        _settings = new SettingsService();
        _settings.Load();
        _controller = new RecordingController(_settings);
        _snapshot = new SnapshotService(_settings);

        _controller.StateChanged += OnStateChanged;
        _controller.RecordingCompleted += OnRecordingCompleted;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) => _recordingBar?.UpdateElapsed(_elapsed.Elapsed);

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_clickPending) { _clickPending = false; PrimaryTrayAction(); }
        };
    }

    public void Startup()
    {
        BuildTray();
        _mainWindow = new MainWindow(_settings, kind => BeginSelection(kind, null), Shutdown);
        StartControlServerIfEnabled();
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        _hotkeys = new HotkeyService();
        _hotkeys.Initialize();
        _hotkeys.Register(HotkeyService.VK_F6, OnSnapshotHotkey); // F6 → snapshot
        _hotkeys.Register(HotkeyService.VK_F7, OnRecordHotkey);   // F7 → record / stop
    }

    private void OnSnapshotHotkey()
    {
        if (_controller.State == RecordingState.Idle)
            BeginSelection(CaptureKind.Image, null);
    }

    private void OnRecordHotkey()
    {
        switch (_controller.State)
        {
            case RecordingState.Recording: _ = StopAsync(); break;
            case RecordingState.Idle: BeginSelection(CaptureKind.Video, null); break;
        }
    }

    // ---- tray -------------------------------------------------------------

    private void BuildTray()
    {
        _tray = new TaskbarIcon
        {
            Icon = TrayIconFactory.CreateIdle(),
            ToolTipText = "Snapture — click to capture",
            Visibility = Visibility.Visible,
        };
        // Defer the single-click action briefly so a double-click can pre-empt it.
        _tray.TrayLeftMouseUp += (_, _) => { _clickPending = true; _clickTimer.Stop(); _clickTimer.Start(); };
        _tray.TrayMouseDoubleClick += (_, _) => { _clickPending = false; _clickTimer.Stop(); ShowSettings(); };
        _tray.TrayBalloonTipClicked += (_, _) => OpenLastSaved();
        _tray.ContextMenu = BuildContextMenu();
    }

    /// <summary>Tray single-click: stop while recording, otherwise start a capture.</summary>
    private void PrimaryTrayAction()
    {
        switch (_controller.State)
        {
            case RecordingState.Recording:
                _ = StopAsync();
                break;
            case RecordingState.Idle:
                BeginSelection(CaptureKind.Video, null);
                break;
            // Selecting/Encoding: ignore.
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        menu.Items.Add(Item("Record video", () => BeginSelection(CaptureKind.Video, null)));
        menu.Items.Add(Item("Take snapshot", () => BeginSelection(CaptureKind.Image, null)));
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(Item("Open library folder", OpenLibrary));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", Shutdown));
        return menu;
    }

    private void ShowSettings()
    {
        _mainWindow ??= new MainWindow(_settings, kind => BeginSelection(kind, null), Shutdown);
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OpenLibrary() => OpenInExplorer(_settings.ResolveLibraryFolder());

    private void OpenLastSaved()
    {
        if (_lastSavedPath is not null && File.Exists(_lastSavedPath))
            Reveal(_lastSavedPath);
        else
            OpenLibrary();
    }

    // ---- selection / recording flow --------------------------------------

    private void BeginSelection(CaptureKind kind, CaptureMode? modeOverride)
    {
        if (_controller.State != RecordingState.Idle)
            return;
        if (!_controller.BeginSelection())
            return;

        var mode = modeOverride ?? (kind == CaptureKind.Image
            ? _settings.Current.SnapshotCaptureMode
            : _settings.Current.DefaultCaptureMode);

        _overlay = new OverlayWindow(kind, mode);
        _overlay.Confirmed += ConfirmAndStart;
        _overlay.Cancelled += () => _ = CancelAsync();
        _overlay.CaptureModeChanged += OnOverlayCaptureModeChanged;
        _overlay.Show();
        _overlay.Activate();
    }

    /// <summary>A mid-pick capture-mode change overrides the saved default for that kind.</summary>
    private void OnOverlayCaptureModeChanged()
    {
        if (_overlay is null) return;
        if (_overlay.Kind == CaptureKind.Image)
            _settings.Current.SnapshotCaptureMode = _overlay.Mode;
        else
            _settings.Current.DefaultCaptureMode = _overlay.Mode;
        _settings.Save(_settings.Current);
    }

    private void ConfirmAndStart()
    {
        var overlay = _overlay;
        var target = overlay?.GetCurrentTarget();
        if (overlay is null || target is null)
            return;

        var kind = overlay.Kind;
        CloseOverlay(); // dim disappears; the rest of the desktop is usable again

        if (kind == CaptureKind.Image)
            _ = TakeSnapshotAsync(target);
        else
        {
            ShowRecordingBar();
            StartRecordingCore(target);
        }
    }

    private async Task TakeSnapshotAsync(CaptureTarget target)
    {
        // Snapshots don't drive the recording state machine; release the
        // Selecting state the overlay reserved so the app returns to Idle.
        await _controller.AbortAsync();

        var result = await _snapshot.CaptureAsync(target);

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (result.Success)
            {
                _lastSavedPath = result.OutputPath;
                Notify("Snapshot saved",
                    $"{Path.GetFileName(result.OutputPath)} — click to open", BalloonIcon.Info);
                if (_settings.Current.RevealAfterSave && result.OutputPath is not null)
                    Reveal(result.OutputPath);
            }
            else
            {
                Notify("Snapshot failed", result.Error ?? "Unknown error", BalloonIcon.Error);
            }
        });

        _server?.Broadcast(new ControlEvent
        {
            Event = "snapshotCompleted",
            State = StateName(_controller.State),
            Data = new { ok = result.Success, path = result.OutputPath, error = result.Error },
        });
    }

    private void ShowRecordingBar()
    {
        if (_recordingBar is not null)
            return;
        _recordingBar = new RecordingBarWindow();
        _recordingBar.StopRequested += () => _ = StopAsync();
        _recordingBar.Show();
    }

    private void StartRecordingCore(CaptureTarget target)
    {
        try
        {
            _controller.StartAsync(target);
            _elapsed.Restart();
            _elapsedTimer.Start();
        }
        catch (Exception ex)
        {
            _elapsedTimer.Stop();
            CloseRecordingBar();
            _ = _controller.AbortAsync();
            Notify("Couldn't start recording", ex.Message, BalloonIcon.Error);
        }
    }

    private async Task StopAsync()
    {
        _elapsedTimer.Stop();
        await _controller.StopAsync();
        // OnRecordingCompleted handles notification + teardown.
    }

    private async Task CancelAsync()
    {
        _elapsedTimer.Stop();
        CloseOverlay();
        CloseRecordingBar();
        await _controller.AbortAsync();
    }

    // ---- controller events (may arrive on a background thread) -----------

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_tray is not null)
            {
                _tray.Icon = e.NewState == RecordingState.Recording
                    ? TrayIconFactory.CreateRecording()
                    : TrayIconFactory.CreateIdle();
                _tray.ToolTipText = e.NewState switch
                {
                    RecordingState.Recording => "Snapture — recording (click to stop)",
                    RecordingState.Encoding => "Snapture — saving…",
                    _ => "Snapture — click to capture",
                };
            }

            if (e.NewState == RecordingState.Idle)
            {
                CloseOverlay();
                CloseRecordingBar();
            }
        });

        _server?.Broadcast(new ControlEvent { Event = "stateChanged", State = StateName(e.NewState) });
    }

    private void OnRecordingCompleted(object? sender, RecordingResult result)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CloseRecordingBar();
            if (result.Success)
            {
                _lastSavedPath = result.OutputPath;
                Notify("Recording saved",
                    $"{Path.GetFileName(result.OutputPath)} ({FormatDuration(result.Duration)}) — click to open",
                    BalloonIcon.Info);
                if (_settings.Current.RevealAfterSave && result.OutputPath is not null)
                    Reveal(result.OutputPath);
            }
            else
            {
                Notify("Recording failed", result.Error ?? "Unknown error", BalloonIcon.Error);
            }
        });

        _server?.Broadcast(new ControlEvent
        {
            Event = "recordingCompleted",
            State = StateName(_controller.State),
            Data = new { ok = result.Success, path = result.OutputPath, error = result.Error },
        });
    }

    // ---- IPC (Stream Deck) ------------------------------------------------

    private void StartControlServerIfEnabled()
    {
        if (!_settings.Current.EnableControlServer)
            return;
        _server = new ControlServer(_settings.Current.ControlServerPort, this)
        {
            Log = msg => Debug.WriteLine($"[ControlServer] {msg}"),
        };
        _server.Start();
    }

    public Task<ControlResponse> HandleAsync(ControlCommand command, CancellationToken cancellationToken)
        => _dispatcher.InvokeAsync(() => Dispatch(command)).Task;

    private ControlResponse Dispatch(ControlCommand command)
    {
        var state = StateName(_controller.State);
        switch (command.Command.ToLowerInvariant())
        {
            case "getstate":
                return ControlResponse.Success(command.Id, state);

            case "getsettings":
                var s = _settings.Current;
                return ControlResponse.Success(command.Id, state, new
                {
                    format = s.OutputFormat.ToString(),
                    mode = s.DefaultCaptureMode.ToString(),
                    frameRate = s.FrameRate,
                    quality = s.Quality,
                    library = _settings.ResolveLibraryFolder(),
                    snapshot = new
                    {
                        format = s.SnapshotFormat.ToString(),
                        mode = s.SnapshotCaptureMode.ToString(),
                        captureCursor = s.SnapshotCaptureCursor,
                        library = _settings.ResolveSnapshotLibraryFolder(),
                    },
                });

            case "start":
                return HandleStart(command);

            case "snapshot":
                return HandleSnapshot(command);

            case "setsnapshotformat":
                if (Enum.TryParse<ImageFormat>(command.GetString("format"), true, out var sfmt))
                {
                    _settings.Current.SnapshotFormat = sfmt;
                    _settings.Save(_settings.Current);
                    return ControlResponse.Success(command.Id, state);
                }
                return ControlResponse.Failure(command.Id, "Unknown snapshot format.", state);

            case "setsnapshotmode":
                if (Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var smode))
                {
                    _settings.Current.SnapshotCaptureMode = smode;
                    _settings.Save(_settings.Current);
                    return ControlResponse.Success(command.Id, state);
                }
                return ControlResponse.Failure(command.Id, "Unknown snapshot mode.", state);

            case "stop":
                if (_controller.State != RecordingState.Recording)
                    return ControlResponse.Failure(command.Id, "Not recording.", state);
                _ = StopAsync();
                return ControlResponse.Success(command.Id, state);

            case "abort":
                _ = CancelAsync();
                return ControlResponse.Success(command.Id, StateName(_controller.State));

            case "setformat":
                if (Enum.TryParse<OutputFormat>(command.GetString("format"), true, out var fmt))
                {
                    _settings.Current.OutputFormat = fmt;
                    _settings.Save(_settings.Current);
                    return ControlResponse.Success(command.Id, state);
                }
                return ControlResponse.Failure(command.Id, "Unknown format.", state);

            case "setmode":
                if (Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var mode))
                {
                    _settings.Current.DefaultCaptureMode = mode;
                    _settings.Save(_settings.Current);
                    return ControlResponse.Success(command.Id, state);
                }
                return ControlResponse.Failure(command.Id, "Unknown mode.", state);

            default:
                return ControlResponse.Failure(command.Id, $"Unknown command '{command.Command}'.", state);
        }
    }

    /// <summary>
    /// IPC start. Display/Window resolve instantly from the cursor (true
    /// one-press Stream Deck capture); Custom opens the selection overlay.
    /// </summary>
    private ControlResponse HandleStart(ControlCommand command)
    {
        if (_controller.State != RecordingState.Idle)
            return ControlResponse.Failure(command.Id, "Already busy.", StateName(_controller.State));

        var mode = Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var m)
            ? m
            : _settings.Current.DefaultCaptureMode;

        if (mode == CaptureMode.Custom)
        {
            BeginSelection(CaptureKind.Video, CaptureMode.Custom);
            return ControlResponse.Success(command.Id, StateName(_controller.State));
        }

        if (!NativeMethods.GetCursorPos(out var p))
            return ControlResponse.Failure(command.Id, "Cursor position unavailable.");

        CaptureTarget? target = mode == CaptureMode.Display
            ? DisplayTargetAt(p.X, p.Y)
            : WindowTargetAt(p.X, p.Y);

        if (target is null)
            return ControlResponse.Failure(command.Id, "No capture target under cursor.");

        ShowRecordingBar();
        StartRecordingCore(target);
        return ControlResponse.Success(command.Id, StateName(_controller.State));
    }

    /// <summary>
    /// IPC snapshot. Mirrors <see cref="HandleStart"/> but produces a still image:
    /// Display/Window capture instantly from the cursor; Custom opens the overlay.
    /// </summary>
    private ControlResponse HandleSnapshot(ControlCommand command)
    {
        if (_controller.State != RecordingState.Idle)
            return ControlResponse.Failure(command.Id, "Already busy.", StateName(_controller.State));

        var mode = Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var m)
            ? m
            : _settings.Current.SnapshotCaptureMode;

        if (mode == CaptureMode.Custom)
        {
            BeginSelection(CaptureKind.Image, CaptureMode.Custom);
            return ControlResponse.Success(command.Id, StateName(_controller.State));
        }

        if (!NativeMethods.GetCursorPos(out var p))
            return ControlResponse.Failure(command.Id, "Cursor position unavailable.");

        CaptureTarget? target = mode == CaptureMode.Display
            ? DisplayTargetAt(p.X, p.Y)
            : WindowTargetAt(p.X, p.Y);

        if (target is null)
            return ControlResponse.Failure(command.Id, "No capture target under cursor.");

        _ = TakeSnapshotAsync(target);
        return ControlResponse.Success(command.Id, StateName(_controller.State));
    }

    private static CaptureTarget DisplayTargetAt(int x, int y)
    {
        var mon = ScreenInfo.MonitorAt(x, y);
        return new CaptureTarget { Mode = CaptureMode.Display, Region = mon.Bounds, Label = mon.Name };
    }

    private CaptureTarget? WindowTargetAt(int x, int y)
    {
        var ignore = _recordingBar is not null
            ? new[] { new WindowInteropHelper(_recordingBar).Handle }
            : Array.Empty<nint>();
        var hit = ScreenInfo.WindowAt(x, y, ignore);
        return hit is { } h
            ? new CaptureTarget { Mode = CaptureMode.Window, Region = h.Bounds, WindowHandle = h.Handle }
            : null;
    }

    // ---- helpers ----------------------------------------------------------

    private void CloseOverlay()
    {
        if (_overlay is null) return;
        var o = _overlay; _overlay = null;
        try { o.Close(); } catch { }
    }

    private void CloseRecordingBar()
    {
        if (_recordingBar is null) return;
        var t = _recordingBar; _recordingBar = null;
        try { t.Close(); } catch { }
    }

    private void Notify(string title, string message, BalloonIcon icon)
        => _tray?.ShowBalloonTip(title, message, icon);

    private static void OpenInExplorer(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch { }
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }

    private static string StateName(RecordingState s) => s.ToString().ToLowerInvariant();

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    public void Shutdown()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        try { _ = _controller.AbortAsync(); } catch { }
        try { if (_server is not null) _ = _server.DisposeAsync(); } catch { }
        try { _hotkeys?.Dispose(); } catch { }
        if (_mainWindow is not null) _mainWindow.AllowClose = true;
        CloseOverlay();
        CloseRecordingBar();
        _tray?.Dispose();
    }
}
