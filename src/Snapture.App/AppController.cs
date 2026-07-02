using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    // Per-session memory for the Stream Deck plugin: the last target and saved
    // file for each capture kind. Not persisted across sessions (by design).
    private CaptureTarget? _lastImageTarget;
    private CaptureTarget? _lastVideoTarget;
    private string? _lastImagePath;
    private string? _lastVideoPath;

    public AppController()
    {
        _dispatcher = Application.Current.Dispatcher;
        _settings = new SettingsService();
        _settings.Load();
        _controller = new RecordingController(_settings);
        _snapshot = new SnapshotService(_settings);

        _controller.StateChanged += OnStateChanged;
        _controller.RecordingCompleted += OnRecordingCompleted;
        _settings.SettingsChanged += (_, _) => { ApplyServerSetting(); ApplyHotkeys(); };

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) =>
        {
            _recordingBar?.UpdateElapsed(_elapsed.Elapsed);
            _server?.Broadcast(new ControlEvent
            {
                Event = "elapsed",
                State = StateName(_controller.State),
                Data = new { seconds = (int)_elapsed.Elapsed.TotalSeconds },
            });
        };

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
        _mainWindow = new MainWindow(_settings, kind => BeginSelection(kind, null), Shutdown,
            IsPluginConnected, PingPlugin,
            suspend => { if (suspend) _hotkeys?.Clear(); else ApplyHotkeys(); },
            () => _ = StopAsync());
        StartControlServerIfEnabled();
        RegisterHotkeys();

        Notify("Snapture is running", "It stays in the tray — click the icon to capture, or F6/F7.", BalloonIcon.Info);
    }

    /// <summary>Bring up the settings window (used when a second launch pokes this instance).</summary>
    public void ShowSettingsWindow() => ShowSettings();

    private void RegisterHotkeys()
    {
        _hotkeys = new HotkeyService();
        _hotkeys.Initialize();
        ApplyHotkeys();
    }

    /// <summary>Re-register the global hotkeys from the current settings.</summary>
    private void ApplyHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Clear();
        var s = _settings.Current;
        if (!s.HotkeysEnabled) return;
        if (s.PickerHotkey.Enabled)
            _hotkeys.Register((uint)s.PickerHotkey.VirtualKey, OnPickerHotkey, (uint)s.PickerHotkey.Modifiers);
        if (s.SnapshotHotkey.Enabled)
            _hotkeys.Register((uint)s.SnapshotHotkey.VirtualKey, OnSnapshotHotkey, (uint)s.SnapshotHotkey.Modifiers);
        if (s.RecordHotkey.Enabled)
            _hotkeys.Register((uint)s.RecordHotkey.VirtualKey, OnRecordHotkey, (uint)s.RecordHotkey.Modifiers);
    }

    /// <summary>The capture kind to default to (resolving "Last used").</summary>
    private CaptureKind ResolveDefaultKind()
    {
        var d = _settings.Current.DefaultSnapKind;
        if (d == "lastused") d = _settings.Current.LastUsedSnapKind;
        return d == "image" ? CaptureKind.Image : CaptureKind.Video;
    }

    private void OnPickerHotkey()
    {
        if (_controller.State == RecordingState.Idle)
            BeginSelection(ResolveDefaultKind(), null);
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
                BeginSelection(ResolveDefaultKind(), null);
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
        _mainWindow ??= new MainWindow(_settings, kind => BeginSelection(kind, null), Shutdown,
            IsPluginConnected, PingPlugin,
            suspend => { if (suspend) _hotkeys?.Clear(); else ApplyHotkeys(); },
            () => _ = StopAsync());
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
        RememberKind(kind);

        if (kind == CaptureKind.Image)
        {
            _lastImageTarget = target;
            _ = TakeSnapshotAsync(target);
        }
        else
        {
            _lastVideoTarget = target;
            ShowRecordingBar();
            StartRecordingCore(target);
        }
    }

    private async Task TakeSnapshotAsync(CaptureTarget target, ImageFormat? formatOverride = null)
    {
        // Snapshots don't drive the recording state machine; release the
        // Selecting state the overlay reserved so the app returns to Idle.
        await _controller.AbortAsync();

        var result = await _snapshot.CaptureAsync(target, formatOverride);

        _ = _dispatcher.BeginInvoke(() =>
        {
            if (result.Success)
            {
                _lastSavedPath = result.OutputPath;
                _lastImagePath = result.OutputPath;
                if (_settings.Current.SnapshotToClipboard && result.OutputPath is not null)
                    CopyImageToClipboard(result.OutputPath);
                if (result.OutputPath is not null)
                    ShowSnapshotBalloon(result.OutputPath);
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

    private void StartRecordingCore(CaptureTarget target, OutputFormat? formatOverride = null)
    {
        try
        {
            _controller.StartAsync(target, formatOverride);
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

            _mainWindow?.SetRecording(e.NewState == RecordingState.Recording);

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
                _lastVideoPath = result.OutputPath;
                if (_settings.Current.VideoToClipboard && result.OutputPath is not null)
                    CopyFileToClipboard(result.OutputPath);
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
        _server.ClientsChanged += () => _dispatcher.BeginInvoke(() => _mainWindow?.RefreshPluginStatus());
        _server.Start();
        _activeServerPort = _settings.Current.ControlServerPort;
    }

    private int _activeServerPort;

    /// <summary>Start/stop/restart the control server to match the current settings.</summary>
    private void ApplyServerSetting()
    {
        var want = _settings.Current.EnableControlServer;
        var port = _settings.Current.ControlServerPort;
        var running = _server is not null;

        if (want && !running)
            StartControlServerIfEnabled();
        else if (!want && running)
            StopControlServer();
        else if (want && running && port != _activeServerPort)
        {
            StopControlServer();
            StartControlServerIfEnabled();
        }

        _mainWindow?.RefreshPluginStatus();
    }

    private void StopControlServer()
    {
        var s = _server;
        _server = null;
        if (s is not null)
            try { _ = s.DisposeAsync(); } catch { }
    }

    /// <summary>Ping connected plugin clients (they flash a message on their keys).</summary>
    private void PingPlugin() => _server?.Broadcast(new ControlEvent { Event = "ping" });

    /// <summary>A plugin counts as connected only while it's still sending heartbeats.</summary>
    private bool IsPluginConnected() =>
        _server is { } s && s.ClientCount > 0 && (DateTime.UtcNow - s.LastActivityUtc).TotalSeconds < 8;

    public Task<ControlResponse> HandleAsync(ControlCommand command, CancellationToken cancellationToken)
        => _dispatcher.InvokeAsync(() => Dispatch(command)).Task;

    private ControlResponse Dispatch(ControlCommand command)
    {
        var state = StateName(_controller.State);
        switch (command.Command.ToLowerInvariant())
        {
            case "getstate":
                return ControlResponse.Success(command.Id, state, new { version = AppVersion() });

            case "getversion":
                return ControlResponse.Success(command.Id, state, new { version = AppVersion() });

            case "heartbeat": // liveness ping from the plugin; receipt already stamped activity
                return ControlResponse.Success(command.Id, state);

            case "getsettings":
                var s = _settings.Current;
                return ControlResponse.Success(command.Id, state, new
                {
                    version = AppVersion(),
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

            case "getdisplays":
                return ControlResponse.Success(command.Id, state, new { displays = DisplayList() });

            case "getwindows":
                return ControlResponse.Success(command.Id, state, new { windows = WindowList() });

            case "identifydisplays":
                IdentifyDisplays();
                return ControlResponse.Success(command.Id, state);

            case "openlibrary":
                OpenInExplorer(string.Equals(command.GetString("kind"), "image", StringComparison.OrdinalIgnoreCase)
                    ? _settings.ResolveSnapshotLibraryFolder()
                    : _settings.ResolveLibraryFolder());
                return ControlResponse.Success(command.Id, state);

            case "openlast":
                return HandleOpenLast(command, state);

            case "start":
                return HandleCapture(command, CaptureKind.Video);

            case "snapshot":
                return HandleCapture(command, CaptureKind.Image);

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
    /// Unified capture entry point for both kinds. Supports (in priority order):
    /// <c>repeat</c> (re-run this session's last target), <c>picker</c> (open the
    /// overlay), an explicit <c>display</c>/<c>window</c> target, or the legacy
    /// cursor-based mode. An optional <c>format</c> overrides the saved default for
    /// this one capture.
    /// </summary>
    private ControlResponse HandleCapture(ControlCommand command, CaptureKind kind)
    {
        if (_controller.State != RecordingState.Idle)
            return ControlResponse.Failure(command.Id, "Already busy.", StateName(_controller.State));

        // Redo the last capture of this kind (session-only memory).
        if (command.GetBool("repeat"))
        {
            var last = kind == CaptureKind.Image ? _lastImageTarget : _lastVideoTarget;
            if (last is null)
                return ControlResponse.Failure(command.Id, "No previous capture to repeat in this session.", StateName(_controller.State));
            return DoCapture(command, kind, last);
        }

        // Open the selection overlay with a starting mode ("default"/null → the
        // configured default). Any format override is persisted so the overlay uses it.
        if (command.GetBool("picker"))
        {
            PersistFormatOverride(kind, command.GetString("format"));
            var picked = Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var pm)
                ? (CaptureMode?)pm
                : null;
            BeginSelection(kind, picked);
            return ControlResponse.Success(command.Id, StateName(_controller.State));
        }

        // A specific display chosen for the action.
        if (command.GetString("display") is { } displayId)
        {
            var t = ResolveDisplayTarget(displayId);
            return t is null
                ? ControlResponse.Failure(command.Id, $"Display '{displayId}' not found.", StateName(_controller.State))
                : DoCapture(command, kind, t);
        }

        // A specific window chosen for the action (by handle, falling back to title).
        if (command.GetString("window") is { } winHandle || command.GetString("windowTitle") is not null)
        {
            var t = ResolveWindowTarget(command.GetString("window"), command.GetString("windowTitle"));
            return t is null
                ? ControlResponse.Failure(command.Id, "The chosen window isn't open right now.", StateName(_controller.State))
                : DoCapture(command, kind, t);
        }

        // Legacy cursor-based: Custom opens the overlay; Display/Window capture
        // whatever is under the cursor instantly.
        var mode = Enum.TryParse<CaptureMode>(command.GetString("mode"), true, out var m)
            ? m
            : (kind == CaptureKind.Image ? _settings.Current.SnapshotCaptureMode : _settings.Current.DefaultCaptureMode);

        if (mode == CaptureMode.Custom)
        {
            BeginSelection(kind, CaptureMode.Custom);
            return ControlResponse.Success(command.Id, StateName(_controller.State));
        }

        if (!NativeMethods.GetCursorPos(out var p))
            return ControlResponse.Failure(command.Id, "Cursor position unavailable.");

        var target = mode == CaptureMode.Display ? DisplayTargetAt(p.X, p.Y) : WindowTargetAt(p.X, p.Y);
        return target is null
            ? ControlResponse.Failure(command.Id, "No capture target under cursor.", StateName(_controller.State))
            : DoCapture(command, kind, target);
    }

    /// <summary>Run an instant (no-overlay) capture of a resolved target, remembering it.</summary>
    private ControlResponse DoCapture(ControlCommand command, CaptureKind kind, CaptureTarget target)
    {
        RememberKind(kind);
        if (kind == CaptureKind.Image)
        {
            _lastImageTarget = target;
            var fmt = Enum.TryParse<ImageFormat>(command.GetString("format"), true, out var f) ? (ImageFormat?)f : null;
            _ = TakeSnapshotAsync(target, fmt);
        }
        else
        {
            _lastVideoTarget = target;
            var fmt = Enum.TryParse<OutputFormat>(command.GetString("format"), true, out var f) ? (OutputFormat?)f : null;
            ShowRecordingBar();
            StartRecordingCore(target, fmt);
        }
        return ControlResponse.Success(command.Id, StateName(_controller.State));
    }

    private void PersistFormatOverride(CaptureKind kind, string? format)
    {
        if (format is null) return;
        if (kind == CaptureKind.Image && Enum.TryParse<ImageFormat>(format, true, out var ifmt))
            _settings.Current.SnapshotFormat = ifmt;
        else if (kind == CaptureKind.Video && Enum.TryParse<OutputFormat>(format, true, out var vfmt))
            _settings.Current.OutputFormat = vfmt;
        else
            return;
        _settings.Save(_settings.Current);
    }

    private ControlResponse HandleOpenLast(ControlCommand command, string state)
    {
        var filter = command.GetString("filter")?.ToLowerInvariant();
        string? path = filter switch
        {
            "image" => _lastImagePath,
            "video" => _lastVideoPath,
            _ => MostRecent(_lastImagePath, _lastVideoPath),
        };
        if (path is null || !File.Exists(path))
            return ControlResponse.Failure(command.Id, "No matching capture saved this session.", state);

        if (string.Equals(command.GetString("action"), "open", StringComparison.OrdinalIgnoreCase))
            OpenFileWithApp(path);
        else
            Reveal(path);
        return ControlResponse.Success(command.Id, state, new { path });
    }

    /// <summary>Open a saved capture with its default app (image viewer / video player).</summary>
    private static void OpenFileWithApp(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }

    /// <summary>Copy a saved image onto the clipboard (best-effort; WebP may not decode).</summary>
    private static void CopyImageToClipboard(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            Clipboard.SetImage(bmp);
        }
        catch { /* unsupported codec or clipboard busy */ }
    }

    /// <summary>Copy a saved file to the clipboard as a file drop (paste it in explorer/chat).</summary>
    private static void CopyFileToClipboard(string path)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
        }
        catch { /* clipboard busy */ }
    }

    /// <summary>Tray toast for a saved snapshot, showing a thumbnail preview.</summary>
    private void ShowSnapshotBalloon(string path)
    {
        if (_tray is null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();

            var panel = new StackPanel { MaxWidth = 260 };
            panel.Children.Add(new TextBlock { Text = "Snapshot saved", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            panel.Children.Add(new TextBlock { Text = Path.GetFileName(path), Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 130 });

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x3B, 0x3B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = panel,
            };
            border.MouseLeftButtonUp += (_, _) => { OpenFileWithApp(path); _tray.CloseBalloon(); };
            _tray.ShowCustomBalloon(border, System.Windows.Controls.Primitives.PopupAnimation.Fade, 5000);
        }
        catch
        {
            Notify("Snapshot saved", Path.GetFileName(path) + " — click to open", BalloonIcon.Info);
        }
    }

    /// <summary>Remember the last capture kind used (for the "Last used" default).</summary>
    private void RememberKind(CaptureKind kind)
    {
        _settings.Current.LastUsedSnapKind = kind == CaptureKind.Image ? "image" : "video";
        _settings.Save(_settings.Current);
    }

    private static string? MostRecent(string? a, string? b)
    {
        bool ea = a is not null && File.Exists(a), eb = b is not null && File.Exists(b);
        if (ea && eb) return File.GetLastWriteTimeUtc(a!) >= File.GetLastWriteTimeUtc(b!) ? a : b;
        return ea ? a : eb ? b : null;
    }

    private CaptureTarget? ResolveDisplayTarget(string id)
    {
        var mon = ScreenInfo.GetMonitors().FirstOrDefault(m => string.Equals(m.Name, id, StringComparison.OrdinalIgnoreCase));
        return mon is null
            ? null
            : new CaptureTarget { Mode = CaptureMode.Display, Region = mon.Bounds, Label = mon.Name };
    }

    private CaptureTarget? ResolveWindowTarget(string? handleStr, string? title)
    {
        if (nint.TryParse(handleStr, out var h) && ScreenInfo.WindowBounds(h) is { } b)
            return new CaptureTarget { Mode = CaptureMode.Window, Region = b, WindowHandle = h, Label = title };

        if (!string.IsNullOrWhiteSpace(title))
        {
            var match = ScreenInfo.GetOpenWindows().FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.Ordinal));
            if (match is not null)
                return new CaptureTarget { Mode = CaptureMode.Window, Region = match.Bounds, WindowHandle = match.Handle, Label = match.Title };
        }
        return null;
    }

    private readonly List<Window> _identifyWindows = new();
    private DispatcherTimer? _identifyTimer;

    /// <summary>Flash a big number on each display for ~2s (dismiss on click too).</summary>
    private void IdentifyDisplays()
    {
        CloseIdentify();
        var monitors = ScreenInfo.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            int side = Math.Max(160, (int)(Math.Min(m.Bounds.Width, m.Bounds.Height) * 0.32));
            int x = m.Bounds.X + (m.Bounds.Width - side) / 2;
            int y = m.Bounds.Y + (m.Bounds.Height - side) / 2;

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x3B, 0x3B)),
                BorderThickness = new Thickness(4),
                CornerRadius = new CornerRadius(24),
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(24),
                    Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold },
                },
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Content = badge,
            };
            w.MouseLeftButtonDown += (_, _) => { try { w.Close(); } catch { } };
            w.Loaded += (_, _) =>
            {
                NativeMethods.SetWindowBoundsPhysical(w, x, y, side, side);
                NativeMethods.MarkToolWindow(w, noActivate: true);
            };
            w.Show();
            _identifyWindows.Add(w);
        }

        _identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _identifyTimer.Tick += (_, _) => { _identifyTimer?.Stop(); _identifyTimer = null; CloseIdentify(); };
        _identifyTimer.Start();
    }

    private void CloseIdentify()
    {
        foreach (var w in _identifyWindows) { try { w.Close(); } catch { } }
        _identifyWindows.Clear();
    }

    private static object[] DisplayList()
    {
        var monitors = ScreenInfo.GetMonitors();
        return monitors.Select((m, i) => (object)new
        {
            id = m.Name,
            label = $"Display {i + 1} — {m.Bounds.Width}×{m.Bounds.Height}" + (m.IsPrimary ? " (primary)" : ""),
            width = m.Bounds.Width,
            height = m.Bounds.Height,
            primary = m.IsPrimary,
        }).ToArray();
    }

    private static object[] WindowList() =>
        ScreenInfo.GetOpenWindows().Select(w => (object)new
        {
            handle = w.Handle.ToString(),
            title = w.Title,
            process = w.Process,
        }).ToArray();

    private static string AppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
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
        CloseIdentify();
        if (_mainWindow is not null) _mainWindow.AllowClose = true;
        CloseOverlay();
        CloseRecordingBar();
        _tray?.Dispose();
    }
}
