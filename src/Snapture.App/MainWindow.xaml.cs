using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Snapture.App.Views;
using Snapture.Core.Encoding;
using Snapture.Core.Models;
using Snapture.Core.Settings;
using Snapture.Core.Update;
using WinInput = System.Windows.Input;

namespace Snapture.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action<CaptureKind> _startCapture;
    private readonly Action _startTextCapture;
    private readonly Action _quit;
    private readonly Func<bool> _isPluginConnected;
    private readonly Action _pingPlugin;
    private readonly Action<bool> _suspendHotkeys;
    private readonly Action _stopRecording;
    private readonly System.Windows.Threading.DispatcherTimer _pluginPoll;
    private bool _loading;
    private bool _recording;
    private string? _ffmpegPath;
    private string? _capturingHotkey; // "picker" | "snap" | "rec" | "copytext" while editing a shortcut

    private UpdateService _updater = null!;
    private UpdateManifest? _manifestCache;
    private ReleaseInfo? _pendingUpdate;

    /// <summary>When false, closing hides to tray instead of exiting.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(SettingsService settings, Action<CaptureKind> startCapture, Action startTextCapture, Action quit,
        Func<bool> isPluginConnected, Action pingPlugin, Action<bool> suspendHotkeys, Action stopRecording)
    {
        _settings = settings;
        _startCapture = startCapture;
        _startTextCapture = startTextCapture;
        _quit = quit;
        _isPluginConnected = isPluginConnected;
        _pingPlugin = pingPlugin;
        _suspendHotkeys = suspendHotkeys;
        _stopRecording = stopRecording;
        InitializeComponent();

        FooterSnapshotButton.Content = CaptureIcons.ScanCamera();
        SetRecording(false);

        WireEvents();
        LoadFromSettings();
        RefreshFfmpegStatus();
        InitUpdates();

        PluginGitHubButton.Click += (_, _) => OpenUrl("https://github.com/Este2013/Snapture_StreamDeck_Plugin");
        PluginStatusButton.Click += (_, _) => OnPluginStatusClick();
        _pluginPoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pluginPoll.Tick += (_, _) => RefreshPluginStatus();
        IsVisibleChanged += (_, _) => { if (IsVisible) { SelectDefaultTab(); _pluginPoll.Start(); RefreshPluginStatus(); } else _pluginPoll.Stop(); };
        RefreshPluginStatus();
    }

    private void WireEvents()
    {
        TabGeneral.Checked += (_, _) => UpdateTab();
        TabSnapshot.Checked += (_, _) => UpdateTab();
        TabVideo.Checked += (_, _) => UpdateTab();
        TabPicker.Checked += (_, _) => UpdateTab();

        foreach (var r in new[] { PosTopLeft, PosTopCenter, PosTopRight, PosLeftCenter, PosRightCenter, PosBottomLeft, PosBottomCenter, PosBottomRight })
            r.Checked += (_, _) => Persist();
        foreach (var r in new[] { SelPosLeft, SelPosRight, SelPosTop, SelPosBottom, SelPosDock })
            r.Checked += (_, _) => Persist();
        ViewShortcutsButton.Click += (_, _) => ShowShortcutsDialog();

        // Snapshot
        SnapFormatPng.Checked += (_, _) => Persist();
        SnapFormatJpeg.Checked += (_, _) => Persist();
        SnapFormatWebp.Checked += (_, _) => Persist();
        SnapModeDisplay.Checked += (_, _) => Persist();
        SnapModeWindow.Checked += (_, _) => Persist();
        SnapModeCustom.Checked += (_, _) => Persist();
        SnapCursorSwitch.Click += (_, _) => Persist();
        SnapClipboardSwitch.Click += (_, _) => Persist();
        SnapOpenLibrary.Click += (_, _) => OpenInExplorer(_settings.ResolveSnapshotLibraryFolder());
        SnapChangeLibrary.Click += (_, _) => ChangeLibrary(CaptureKind.Image);
        SnapResetLibrary.Click += (_, _) => { _settings.Current.SnapshotLibraryFolder = string.Empty; Persist(); LoadFromSettings(); };

        // Video
        VidFormatMp4.Checked += (_, _) => Persist();
        VidFormatWebp.Checked += (_, _) => Persist();
        VidFormatGif.Checked += (_, _) => Persist();
        VidModeDisplay.Checked += (_, _) => Persist();
        VidModeWindow.Checked += (_, _) => Persist();
        VidModeCustom.Checked += (_, _) => Persist();
        VidCursorSwitch.Click += (_, _) => Persist();
        VidClipboardSwitch.Click += (_, _) => Persist();
        FpsSlider.ValueChanged += (_, _) => { FpsValue.Text = $"{(int)FpsSlider.Value} fps"; Persist(); };
        QualitySlider.ValueChanged += (_, _) => { QualityValue.Text = $"{(int)QualitySlider.Value}"; Persist(); };
        VidOpenLibrary.Click += (_, _) => OpenInExplorer(_settings.ResolveLibraryFolder());
        VidChangeLibrary.Click += (_, _) => ChangeLibrary(CaptureKind.Video);
        VidResetLibrary.Click += (_, _) => { _settings.Current.LibraryFolder = string.Empty; Persist(); LoadFromSettings(); };

        // General
        DefLastUsed.Checked += (_, _) => Persist();
        DefSnapshot.Checked += (_, _) => Persist();
        DefVideo.Checked += (_, _) => Persist();
        ServerCheck.Click += (_, _) => { Persist(); RefreshPluginStatus(); };
        PortBox.LostFocus += (_, _) => Persist();
        FfmpegStatus.MouseLeftButtonUp += (_, _) => OpenFfmpegFolder();

        LaunchOnStartupSwitch.Click += (_, _) => { if (!_loading) StartupRegistration.Set(LaunchOnStartupSwitch.IsChecked == true); };
        HotkeysMasterSwitch.Click += (_, _) => { Persist(); UpdateHotkeyEnabled(); };
        PickerHotkeySwitch.Click += (_, _) => Persist();
        SnapHotkeySwitch.Click += (_, _) => Persist();
        RecHotkeySwitch.Click += (_, _) => Persist();
        CopyTextHotkeySwitch.Click += (_, _) => Persist();
        PickerHotkeyChange.Click += (_, _) => BeginCaptureHotkey("picker");
        SnapHotkeyChange.Click += (_, _) => BeginCaptureHotkey("snap");
        RecHotkeyChange.Click += (_, _) => BeginCaptureHotkey("rec");
        CopyTextHotkeyChange.Click += (_, _) => BeginCaptureHotkey("copytext");
        PreviewKeyDown += OnPreviewKeyDown;

        WireExpandable(PickerExpandButton, PickerDetailsRow);
        WireExpandable(SnapExpandButton, SnapDetailsRow);
        WireExpandable(RecExpandButton, RecDetailsRow);
        WireExpandable(CopyTextExpandButton, CopyTextDetailsRow);

        FooterCopyTextButton.Click += (_, _) => { Hide(); _startTextCapture(); };
        FooterSnapshotButton.Click += (_, _) => { Hide(); _startCapture(CaptureKind.Image); };
        FooterRecordButton.Click += (_, _) => { if (_recording) _stopRecording(); else { Hide(); _startCapture(CaptureKind.Video); } };
        GitHubButton.Click += (_, _) => OpenUrl("https://github.com/Este2013/Snapture");
        LogButton.Click += (_, _) => OpenLog();
        QuitButton.Click += (_, _) => _quit();
    }

    /// <summary>Toggle a shortcut's details row (current key + Change) and flip its chevron.</summary>
    private static void WireExpandable(Button expandButton, FrameworkElement detailsRow)
    {
        expandButton.Click += (_, _) =>
        {
            bool expanded = detailsRow.Visibility == Visibility.Visible;
            detailsRow.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            expandButton.Content = expanded ? "" : ""; // chevron down (collapsed) / up (expanded)
        };
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = _settings.Current;

        SnapFormatPng.IsChecked = s.SnapshotFormat == ImageFormat.Png;
        SnapFormatJpeg.IsChecked = s.SnapshotFormat == ImageFormat.Jpeg;
        SnapFormatWebp.IsChecked = s.SnapshotFormat == ImageFormat.WebP;
        SnapModeDisplay.IsChecked = s.SnapshotCaptureMode == CaptureMode.Display;
        SnapModeWindow.IsChecked = s.SnapshotCaptureMode == CaptureMode.Window;
        SnapModeCustom.IsChecked = s.SnapshotCaptureMode == CaptureMode.Custom;
        SnapCursorSwitch.IsChecked = s.SnapshotCaptureCursor;
        SnapClipboardSwitch.IsChecked = s.SnapshotToClipboard;

        VidFormatMp4.IsChecked = s.OutputFormat == OutputFormat.Mp4;
        VidFormatWebp.IsChecked = s.OutputFormat == OutputFormat.WebP;
        VidFormatGif.IsChecked = s.OutputFormat == OutputFormat.Gif;
        VidModeDisplay.IsChecked = s.DefaultCaptureMode == CaptureMode.Display;
        VidModeWindow.IsChecked = s.DefaultCaptureMode == CaptureMode.Window;
        VidModeCustom.IsChecked = s.DefaultCaptureMode == CaptureMode.Custom;
        FpsSlider.Value = s.FrameRate;
        FpsValue.Text = $"{s.FrameRate} fps";
        QualitySlider.Value = s.Quality;
        QualityValue.Text = $"{s.Quality}";
        VidCursorSwitch.IsChecked = s.CaptureCursor;
        VidClipboardSwitch.IsChecked = s.VideoToClipboard;

        DefLastUsed.IsChecked = s.DefaultSnapKind == "lastused";
        DefSnapshot.IsChecked = s.DefaultSnapKind == "image";
        DefVideo.IsChecked = s.DefaultSnapKind == "video";
        LaunchOnStartupSwitch.IsChecked = StartupRegistration.IsEnabled();
        HotkeysMasterSwitch.IsChecked = s.HotkeysEnabled;
        PickerHotkeySwitch.IsChecked = s.PickerHotkey.Enabled;
        SnapHotkeySwitch.IsChecked = s.SnapshotHotkey.Enabled;
        RecHotkeySwitch.IsChecked = s.RecordHotkey.Enabled;
        CopyTextHotkeySwitch.IsChecked = s.CopyTextHotkey.Enabled;
        LoadHotkeyText();
        UpdateHotkeyEnabled();

        ServerCheck.IsChecked = s.EnableControlServer;
        PortBox.Text = s.ControlServerPort.ToString();

        SetPickerPositionRadio(s.PickerBarPosition);
        SetSelPlacementRadio(s.SelectionToolbarPlacement);

        SelectDefaultTab();

        UpdateLibraryUi();
        _loading = false;
        UpdateTab();
    }

    /// <summary>The settings window always opens on the General tab (re-applied on each open).</summary>
    private void SelectDefaultTab()
    {
        TabGeneral.IsChecked = true;
    }

    private void UpdateTab()
    {
        GeneralPanel.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SnapshotPanel.Visibility = TabSnapshot.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = TabVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PickerPanel.Visibility = TabPicker.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Briefly pulse a red wash over the window to draw attention.</summary>
    public void FlashAttention()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.5, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        FlashOverlay.BeginAnimation(OpacityProperty, anim);
    }

    private ImageFormat SnapshotFormat() =>
        SnapFormatJpeg.IsChecked == true ? ImageFormat.Jpeg
        : SnapFormatWebp.IsChecked == true ? ImageFormat.WebP
        : ImageFormat.Png;

    private OutputFormat VideoFormat() =>
        VidFormatWebp.IsChecked == true ? OutputFormat.WebP
        : VidFormatGif.IsChecked == true ? OutputFormat.Gif
        : OutputFormat.Mp4;

    private static CaptureMode ModeOf(bool display, bool window) =>
        display ? CaptureMode.Display : window ? CaptureMode.Window : CaptureMode.Custom;

    private void Persist()
    {
        if (_loading) return;
        var s = _settings.Current;

        s.SnapshotFormat = SnapshotFormat();
        s.SnapshotCaptureMode = ModeOf(SnapModeDisplay.IsChecked == true, SnapModeWindow.IsChecked == true);
        s.SnapshotCaptureCursor = SnapCursorSwitch.IsChecked == true;
        s.SnapshotToClipboard = SnapClipboardSwitch.IsChecked == true;

        s.OutputFormat = VideoFormat();
        s.DefaultCaptureMode = ModeOf(VidModeDisplay.IsChecked == true, VidModeWindow.IsChecked == true);
        s.FrameRate = (int)FpsSlider.Value;
        s.Quality = (int)QualitySlider.Value;
        s.CaptureCursor = VidCursorSwitch.IsChecked == true;
        s.VideoToClipboard = VidClipboardSwitch.IsChecked == true;

        s.DefaultSnapKind = DefSnapshot.IsChecked == true ? "image" : DefVideo.IsChecked == true ? "video" : "lastused";
        s.HotkeysEnabled = HotkeysMasterSwitch.IsChecked == true;
        s.PickerHotkey.Enabled = PickerHotkeySwitch.IsChecked == true;
        s.SnapshotHotkey.Enabled = SnapHotkeySwitch.IsChecked == true;
        s.RecordHotkey.Enabled = RecHotkeySwitch.IsChecked == true;
        s.CopyTextHotkey.Enabled = CopyTextHotkeySwitch.IsChecked == true;

        s.EnableControlServer = ServerCheck.IsChecked == true;
        if (int.TryParse(PortBox.Text, out var port) && port is > 0 and < 65536)
            s.ControlServerPort = port;

        s.PickerBarPosition = SelectedPickerPosition();
        s.SelectionToolbarPlacement = SelectedSelPlacement();

        _settings.Save(s);
        UpdateLibraryUi();
    }

    // ---- picker toolbar position ----------------------------------------

    private PickerBarPosition SelectedPickerPosition() =>
        PosTopLeft.IsChecked == true ? PickerBarPosition.TopLeft
        : PosTopRight.IsChecked == true ? PickerBarPosition.TopRight
        : PosLeftCenter.IsChecked == true ? PickerBarPosition.LeftCenter
        : PosRightCenter.IsChecked == true ? PickerBarPosition.RightCenter
        : PosBottomLeft.IsChecked == true ? PickerBarPosition.BottomLeft
        : PosBottomCenter.IsChecked == true ? PickerBarPosition.BottomCenter
        : PosBottomRight.IsChecked == true ? PickerBarPosition.BottomRight
        : PickerBarPosition.TopCenter;

    private void SetPickerPositionRadio(PickerBarPosition p)
    {
        PosTopLeft.IsChecked = p == PickerBarPosition.TopLeft;
        PosTopCenter.IsChecked = p == PickerBarPosition.TopCenter;
        PosTopRight.IsChecked = p == PickerBarPosition.TopRight;
        PosLeftCenter.IsChecked = p == PickerBarPosition.LeftCenter;
        PosRightCenter.IsChecked = p == PickerBarPosition.RightCenter;
        PosBottomLeft.IsChecked = p == PickerBarPosition.BottomLeft;
        PosBottomCenter.IsChecked = p == PickerBarPosition.BottomCenter;
        PosBottomRight.IsChecked = p == PickerBarPosition.BottomRight;
    }

    private SelectionToolbarPlacement SelectedSelPlacement() =>
        SelPosRight.IsChecked == true ? SelectionToolbarPlacement.Right
        : SelPosTop.IsChecked == true ? SelectionToolbarPlacement.Top
        : SelPosBottom.IsChecked == true ? SelectionToolbarPlacement.Bottom
        : SelPosDock.IsChecked == true ? SelectionToolbarPlacement.DockToMain
        : SelectionToolbarPlacement.Left;

    private void SetSelPlacementRadio(SelectionToolbarPlacement p)
    {
        SelPosLeft.IsChecked = p == SelectionToolbarPlacement.Left;
        SelPosRight.IsChecked = p == SelectionToolbarPlacement.Right;
        SelPosTop.IsChecked = p == SelectionToolbarPlacement.Top;
        SelPosBottom.IsChecked = p == SelectionToolbarPlacement.Bottom;
        SelPosDock.IsChecked = p == SelectionToolbarPlacement.DockToMain;
    }

    private void ShowShortcutsDialog()
    {
        const string text =
            "Enter — Confirm the capture\n" +
            "Esc — Cancel\n\n" +
            "R — Reload the last capture's position (older each press)\n" +
            "Shift+R — Step forward again through positions\n\n" +
            "Ctrl+Z / Ctrl+Y — Undo / redo selection changes\n" +
            "Arrows — Nudge the selection by 1px (accelerates when held)\n\n" +
            "Scroll — before a selection: walk the tree of UI elements;\n" +
            "               after: grow / shrink the selection\n" +
            "Click / drag — Snap to a UI area, or drag a custom rectangle";
        new Views.ReleaseNotesWindow("Picker keyboard shortcuts", text) { Owner = this }.ShowDialog();
    }

    // ---- global shortcuts editing ----------------------------------------

    private void LoadHotkeyText()
    {
        PickerHotkeyText.Text = _settings.Current.PickerHotkey.Display;
        SnapHotkeyText.Text = _settings.Current.SnapshotHotkey.Display;
        RecHotkeyText.Text = _settings.Current.RecordHotkey.Display;
        CopyTextHotkeyText.Text = _settings.Current.CopyTextHotkey.Display;
    }

    private void UpdateHotkeyEnabled()
    {
        bool on = HotkeysMasterSwitch.IsChecked == true;
        foreach (var c in new UIElement[]
        {
            PickerHotkeySwitch, PickerHotkeyChange, SnapHotkeySwitch, SnapHotkeyChange, RecHotkeySwitch, RecHotkeyChange,
            CopyTextHotkeySwitch, CopyTextHotkeyChange,
        })
            c.IsEnabled = on;
    }

    private HotkeyBinding CapturingBinding() => _capturingHotkey switch
    {
        "picker" => _settings.Current.PickerHotkey,
        "snap" => _settings.Current.SnapshotHotkey,
        "copytext" => _settings.Current.CopyTextHotkey,
        _ => _settings.Current.RecordHotkey,
    };

    private TextBlock CapturingText() => _capturingHotkey switch
    {
        "picker" => PickerHotkeyText,
        "snap" => SnapHotkeyText,
        "copytext" => CopyTextHotkeyText,
        _ => RecHotkeyText,
    };

    private void BeginCaptureHotkey(string which)
    {
        _capturingHotkey = which;
        _suspendHotkeys(true); // so pressing the current hotkey doesn't fire it mid-capture
        CapturingText().Text = "Press keys…";
    }

    private void OnPreviewKeyDown(object sender, WinInput.KeyEventArgs e)
    {
        if (_capturingHotkey is null) return;
        var key = e.Key == WinInput.Key.System ? e.SystemKey : e.Key;
        if (key is WinInput.Key.LeftCtrl or WinInput.Key.RightCtrl or WinInput.Key.LeftAlt or WinInput.Key.RightAlt
            or WinInput.Key.LeftShift or WinInput.Key.RightShift or WinInput.Key.LWin or WinInput.Key.RWin)
            return; // wait for a non-modifier key

        e.Handled = true;
        if (key == WinInput.Key.Escape) { _capturingHotkey = null; _suspendHotkeys(false); LoadHotkeyText(); return; }

        int mods = ModsToWin32(WinInput.Keyboard.Modifiers);
        int vk = WinInput.KeyInterop.VirtualKeyFromKey(key);
        var binding = CapturingBinding();
        binding.Modifiers = mods;
        binding.VirtualKey = vk;
        binding.Display = FormatHotkey(mods, key);
        _capturingHotkey = null;

        _settings.Save(_settings.Current); // re-registers hotkeys in AppController
        _suspendHotkeys(false);
        LoadHotkeyText();
    }

    private static int ModsToWin32(WinInput.ModifierKeys m)
    {
        int r = 0;
        if (m.HasFlag(WinInput.ModifierKeys.Alt)) r |= 1;
        if (m.HasFlag(WinInput.ModifierKeys.Control)) r |= 2;
        if (m.HasFlag(WinInput.ModifierKeys.Shift)) r |= 4;
        if (m.HasFlag(WinInput.ModifierKeys.Windows)) r |= 8;
        return r;
    }

    private static string FormatHotkey(int mods, WinInput.Key key)
    {
        var sb = new StringBuilder();
        if ((mods & 2) != 0) sb.Append("Ctrl+");
        if ((mods & 1) != 0) sb.Append("Alt+");
        if ((mods & 4) != 0) sb.Append("Shift+");
        if ((mods & 8) != 0) sb.Append("Win+");
        sb.Append(key);
        return sb.ToString();
    }

    // ---- updates ----------------------------------------------------------

    private void InitUpdates()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        _updater = new UpdateService(v);
        VersionText.Text = ShortVersion(v);

        HeaderBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        CloseXButton.Click += (_, _) => Hide();
        ReleaseNotesButton.Click += async (_, _) => await ShowReleaseNotesAsync();
        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        UpdateBellButton.Click += (_, _) => ShowUpdateDialog();

        _ = CheckForUpdatesAsync(manual: false);
    }

    private static string ShortVersion(Version v) => $"v{v.Major}.{v.Minor}.{v.Build}";

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual) CheckUpdatesButton.IsEnabled = false;
        var manifest = await _updater.FetchManifestAsync();
        if (manual) CheckUpdatesButton.IsEnabled = true;

        if (manifest is null)
        {
            if (manual)
                MessageBox.Show(this, "Couldn't reach the update server. Check your connection and try again.",
                    "Snapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _manifestCache = manifest;
        _pendingUpdate = _updater.FindNewer(manifest);

        if (_pendingUpdate is not null)
        {
            UpdateBellButton.ToolTip =
                $"A new version is ready to install: {_pendingUpdate.Version}; " +
                $"you currently have {ShortVersion(_updater.CurrentVersion)[1..]}";
            UpdateBellButton.Visibility = Visibility.Visible;
            CheckUpdatesButton.Visibility = Visibility.Collapsed;
            // A manual check that turns up an update jumps straight to the dialog.
            if (manual) ShowUpdateDialog();
        }
        else
        {
            UpdateBellButton.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.Visibility = Visibility.Visible;
            if (manual)
                MessageBox.Show(this, $"You're running the latest version ({ShortVersion(_updater.CurrentVersion)}).",
                    "Snapture", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowUpdateDialog()
    {
        if (_pendingUpdate is null) return;
        new ReleaseNotesWindow(
            $"Snapture {_pendingUpdate.Version} is available",
            _pendingUpdate.Notes ?? "",
            _updater, _pendingUpdate,
            subtitle: $"You have {ShortVersion(_updater.CurrentVersion)[1..]}") { Owner = this }.ShowDialog();
    }

    /// <summary>Open the update dialog (from the startup "update available" toast).</summary>
    public async Task TriggerUpdateDialog()
    {
        if (_pendingUpdate is null)
            await CheckForUpdatesAsync(manual: false);
        if (_pendingUpdate is not null)
            ShowUpdateDialog();
    }

    private async Task ShowReleaseNotesAsync()
    {
        var manifest = _manifestCache ?? await _updater.FetchManifestAsync();
        if (manifest is null || manifest.Releases.Count == 0)
        {
            new ReleaseNotesWindow("Release notes", "Release notes are unavailable right now.") { Owner = this }.ShowDialog();
            return;
        }

        _manifestCache = manifest;
        var releases = manifest.Releases.OrderByDescending(r => r.SemVer).ToList();
        // Open on the version you're running (or the newest if it isn't listed).
        var cur = _updater.CurrentVersion;
        int idx = releases.FindIndex(r =>
            r.SemVer.Major == cur.Major && r.SemVer.Minor == cur.Minor && r.SemVer.Build == cur.Build);
        if (idx < 0) idx = 0;
        new ReleaseNotesWindow(releases, idx) { Owner = this }.ShowDialog();
    }

    /// <summary>Toggle the footer record button between "record" and "stop recording".</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        if (recording)
        {
            FooterRecordButton.Style = (Style)FindResource("RecordButton");
            FooterRecordButton.Content = CaptureIcons.Stop();
            FooterRecordButton.ToolTip = "Stop recording";
        }
        else
        {
            FooterRecordButton.Style = (Style)FindResource("FooterCaptureButton");
            FooterRecordButton.Content = CaptureIcons.Record();
            FooterRecordButton.ToolTip = "Start recording";
        }
    }

    // ---- plugin connection indicator -------------------------------------

    private enum PluginState { ServerOff, Connected, Marketplace, StartSd }
    private PluginState _pluginState = PluginState.StartSd;

    private static bool StreamDeckRunning() => Process.GetProcessesByName("StreamDeck").Length > 0;

    public void RefreshPluginStatus()
    {
        PluginStatusButton.IsEnabled = _settings.Current.EnableControlServer;
        if (!_settings.Current.EnableControlServer)
        {
            _pluginState = PluginState.ServerOff;
            PluginStatusButton.Content = Dot(Colors.Gray);
            PluginStatusButton.ToolTip = "Stream Deck server is off — the plugin can't connect";
            return;
        }

        if (_isPluginConnected())
        {
            _pluginState = PluginState.Connected;
            PluginStatusButton.Content = Dot(Colors.LimeGreen);
            PluginStatusButton.ToolTip = "Connected to plugin — click to ping";
        }
        else if (StreamDeckRunning())
        {
            _pluginState = PluginState.Marketplace;
            PluginStatusButton.Content = new TextBlock { Text = "" }; // Elgato Marketplace / shop glyph
            PluginStatusButton.ToolTip = "Get the Snapture plugin on the Elgato Marketplace";
        }
        else
        {
            _pluginState = PluginState.StartSd;
            PluginStatusButton.Content = Dot(Color.FromRgb(0xE2, 0x3B, 0x3B));
            PluginStatusButton.ToolTip = "Start Elgato Stream Deck";
        }
    }

    private static System.Windows.Shapes.Ellipse Dot(Color c) =>
        new() { Width = 12, Height = 12, Fill = new SolidColorBrush(c) };

    private void OnPluginStatusClick()
    {
        switch (_pluginState)
        {
            case PluginState.Connected:
                _pingPlugin();
                PluginStatusButton.ToolTip = "Ping sent!";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                t.Tick += (_, _) => { t.Stop(); RefreshPluginStatus(); };
                t.Start();
                break;
            case PluginState.Marketplace:
                OpenUrl("https://marketplace.elgato.com/");
                break;
            case PluginState.StartSd:
                StartStreamDeck();
                break;
        }
    }

    private static void StartStreamDeck()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Elgato", "StreamDeck", "StreamDeck.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Elgato", "StreamDeck", "StreamDeck.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) { try { Process.Start(new ProcessStartInfo(c) { UseShellExecute = true }); } catch { } return; }
    }

    // ---- library / ffmpeg -------------------------------------------------

    private void UpdateLibraryUi()
    {
        SnapLibraryPathText.Text = _settings.ResolveSnapshotLibraryFolder();
        SnapResetLibrary.IsEnabled = !string.IsNullOrWhiteSpace(_settings.Current.SnapshotLibraryFolder);
        VidLibraryPathText.Text = _settings.ResolveLibraryFolder();
        VidResetLibrary.IsEnabled = !string.IsNullOrWhiteSpace(_settings.Current.LibraryFolder);
    }

    private void ChangeLibrary(CaptureKind kind)
    {
        var current = kind == CaptureKind.Image
            ? _settings.ResolveSnapshotLibraryFolder()
            : _settings.ResolveLibraryFolder();

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = kind == CaptureKind.Image ? "Choose the snapshot folder" : "Choose the recording folder",
            InitialDirectory = current,
        };
        if (dlg.ShowDialog(this) == true)
        {
            if (kind == CaptureKind.Image) _settings.Current.SnapshotLibraryFolder = dlg.FolderName;
            else _settings.Current.LibraryFolder = dlg.FolderName;
            Persist();
            LoadFromSettings();
        }
    }

    private void RefreshFfmpegStatus()
    {
        if (FfmpegLocator.Verify(null, out var path))
        {
            _ffmpegPath = path;
            FfmpegStatus.Text = $"✓ ffmpeg ready — {path}";
            FfmpegStatus.ToolTip = $"{path}\n(click to open folder)";
        }
        else
        {
            _ffmpegPath = null;
            FfmpegStatus.Text = "⚠ ffmpeg not found — place ffmpeg.exe next to Snapture.exe or on PATH";
            FfmpegStatus.ToolTip = null;
        }
    }

    private void OpenFfmpegFolder()
    {
        if (_ffmpegPath is null) return;
        var dir = Path.GetDirectoryName(_ffmpegPath);
        if (dir is not null) OpenInExplorer(dir);
    }

    private static void OpenInExplorer(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch
        {
            // No default handler for .log → reveal it in Explorer instead.
            try { Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\""); } catch { }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
