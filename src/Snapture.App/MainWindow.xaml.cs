using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Snapture.Core.Encoding;
using Snapture.Core.Models;
using Snapture.Core.Settings;

namespace Snapture.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action<CaptureKind> _startCapture;
    private bool _loading;
    private string? _ffmpegPath;

    /// <summary>When false, closing hides to tray instead of exiting.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(SettingsService settings, Action<CaptureKind> startCapture)
    {
        _settings = settings;
        _startCapture = startCapture;
        InitializeComponent();

        WireEvents();
        LoadFromSettings();
        RefreshFfmpegStatus();
    }

    private void WireEvents()
    {
        TabSnapshot.Checked += (_, _) => UpdateTab();
        TabVideo.Checked += (_, _) => UpdateTab();

        // Snapshot settings
        SnapFormatPng.Checked += (_, _) => Persist();
        SnapFormatJpeg.Checked += (_, _) => Persist();
        SnapFormatWebp.Checked += (_, _) => Persist();
        SnapModeDisplay.Checked += (_, _) => Persist();
        SnapModeWindow.Checked += (_, _) => Persist();
        SnapModeCustom.Checked += (_, _) => Persist();
        SnapCursorSwitch.Click += (_, _) => Persist();
        SnapOpenLibrary.Click += (_, _) => OpenInExplorer(_settings.ResolveSnapshotLibraryFolder());
        SnapChangeLibrary.Click += (_, _) => ChangeLibrary(CaptureKind.Image);
        SnapResetLibrary.Click += (_, _) => { _settings.Current.SnapshotLibraryFolder = string.Empty; Persist(); LoadFromSettings(); };

        // Video settings
        VidFormatMp4.Checked += (_, _) => Persist();
        VidFormatWebp.Checked += (_, _) => Persist();
        VidFormatGif.Checked += (_, _) => Persist();
        VidModeDisplay.Checked += (_, _) => Persist();
        VidModeWindow.Checked += (_, _) => Persist();
        VidModeCustom.Checked += (_, _) => Persist();
        VidCursorSwitch.Click += (_, _) => Persist();
        FpsSlider.ValueChanged += (_, _) => { FpsValue.Text = $"{(int)FpsSlider.Value} fps"; Persist(); };
        QualitySlider.ValueChanged += (_, _) => { QualityValue.Text = $"{(int)QualitySlider.Value}"; Persist(); };
        VidOpenLibrary.Click += (_, _) => OpenInExplorer(_settings.ResolveLibraryFolder());
        VidChangeLibrary.Click += (_, _) => ChangeLibrary(CaptureKind.Video);
        VidResetLibrary.Click += (_, _) => { _settings.Current.LibraryFolder = string.Empty; Persist(); LoadFromSettings(); };

        // Shared
        ServerCheck.Click += (_, _) => Persist();
        PortBox.LostFocus += (_, _) => Persist();
        FfmpegStatus.MouseLeftButtonUp += (_, _) => OpenFfmpegFolder();

        StartButton.Click += (_, _) => { Hide(); _startCapture(SelectedKind()); };
        CloseButton.Click += (_, _) => Hide();
    }

    private CaptureKind SelectedKind() =>
        TabSnapshot.IsChecked == true ? CaptureKind.Image : CaptureKind.Video;

    private void LoadFromSettings()
    {
        _loading = true;
        var s = _settings.Current;

        // Snapshot
        SnapFormatPng.IsChecked = s.SnapshotFormat == ImageFormat.Png;
        SnapFormatJpeg.IsChecked = s.SnapshotFormat == ImageFormat.Jpeg;
        SnapFormatWebp.IsChecked = s.SnapshotFormat == ImageFormat.WebP;
        SnapModeDisplay.IsChecked = s.SnapshotCaptureMode == CaptureMode.Display;
        SnapModeWindow.IsChecked = s.SnapshotCaptureMode == CaptureMode.Window;
        SnapModeCustom.IsChecked = s.SnapshotCaptureMode == CaptureMode.Custom;
        SnapCursorSwitch.IsChecked = s.SnapshotCaptureCursor;

        // Video
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

        // Shared
        ServerCheck.IsChecked = s.EnableControlServer;
        PortBox.Text = s.ControlServerPort.ToString();

        // Default to the Snapshot tab the first time only.
        if (TabSnapshot.IsChecked != true && TabVideo.IsChecked != true)
            TabSnapshot.IsChecked = true;

        UpdateLibraryUi();
        _loading = false;
        UpdateTab();
    }

    private void UpdateTab()
    {
        bool snapshot = TabSnapshot.IsChecked == true;
        SnapshotPanel.Visibility = snapshot ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = snapshot ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = snapshot ? "📷 Take snapshot" : "● Start recording";
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

        s.OutputFormat = VideoFormat();
        s.DefaultCaptureMode = ModeOf(VidModeDisplay.IsChecked == true, VidModeWindow.IsChecked == true);
        s.FrameRate = (int)FpsSlider.Value;
        s.Quality = (int)QualitySlider.Value;
        s.CaptureCursor = VidCursorSwitch.IsChecked == true;

        s.EnableControlServer = ServerCheck.IsChecked == true;
        if (int.TryParse(PortBox.Text, out var port) && port is > 0 and < 65536)
            s.ControlServerPort = port;

        _settings.Save(s);
        UpdateLibraryUi();
    }

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
            if (kind == CaptureKind.Image)
                _settings.Current.SnapshotLibraryFolder = dlg.FolderName;
            else
                _settings.Current.LibraryFolder = dlg.FolderName;
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
