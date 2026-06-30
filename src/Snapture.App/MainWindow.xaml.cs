using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Snapture.Core.Encoding;
using Snapture.Core.Models;
using Snapture.Core.Settings;

namespace Snapture.App;

public partial class MainWindow : Window
{
    private sealed record FormatItem(string Label, OutputFormat Format) { public override string ToString() => Label; }

    private readonly SettingsService _settings;
    private readonly Action _startCapture;
    private bool _loading;
    private string? _ffmpegPath;

    /// <summary>When false, closing hides to tray instead of exiting.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(SettingsService settings, Action startCapture)
    {
        _settings = settings;
        _startCapture = startCapture;
        InitializeComponent();

        FormatCombo.ItemsSource = new[]
        {
            new FormatItem("MP4 (H.264)", OutputFormat.Mp4),
            new FormatItem("Animated WebP", OutputFormat.WebP),
            new FormatItem("GIF", OutputFormat.Gif),
        };

        WireEvents();
        LoadFromSettings();
        RefreshFfmpegStatus();
    }

    private void WireEvents()
    {
        FormatCombo.SelectionChanged += (_, _) => Persist();
        ModeDisplay.Checked += (_, _) => Persist();
        ModeWindow.Checked += (_, _) => Persist();
        ModeCustom.Checked += (_, _) => Persist();
        CursorCheck.Click += (_, _) => Persist();
        ServerCheck.Click += (_, _) => Persist();
        FpsSlider.ValueChanged += (_, _) => { FpsValue.Text = $"{(int)FpsSlider.Value} fps"; Persist(); };
        QualitySlider.ValueChanged += (_, _) => { QualityValue.Text = $"{(int)QualitySlider.Value}"; Persist(); };
        PortBox.LostFocus += (_, _) => Persist();

        StartButton.Click += (_, _) => { Hide(); _startCapture(); };
        CloseButton.Click += (_, _) => Hide();
        OpenLibraryButton.Click += (_, _) => OpenLibrary();
        ChangeLibraryButton.Click += (_, _) => ChangeLibrary();
        ResetLibraryButton.Click += (_, _) => { _settings.Current.LibraryFolder = string.Empty; Persist(); LoadFromSettings(); };
        FfmpegStatus.MouseLeftButtonUp += (_, _) => OpenFfmpegFolder();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = _settings.Current;

        FormatCombo.SelectedItem = ((IEnumerable<FormatItem>)FormatCombo.ItemsSource).First(f => f.Format == s.OutputFormat);
        ModeDisplay.IsChecked = s.DefaultCaptureMode == CaptureMode.Display;
        ModeWindow.IsChecked = s.DefaultCaptureMode == CaptureMode.Window;
        ModeCustom.IsChecked = s.DefaultCaptureMode == CaptureMode.Custom;
        FpsSlider.Value = s.FrameRate;
        FpsValue.Text = $"{s.FrameRate} fps";
        QualitySlider.Value = s.Quality;
        QualityValue.Text = $"{s.Quality}";
        CursorCheck.IsChecked = s.CaptureCursor;
        ServerCheck.IsChecked = s.EnableControlServer;
        PortBox.Text = s.ControlServerPort.ToString();

        UpdateLibraryUi();
        _loading = false;
    }

    private CaptureMode SelectedMode() =>
        ModeDisplay.IsChecked == true ? CaptureMode.Display
        : ModeWindow.IsChecked == true ? CaptureMode.Window
        : CaptureMode.Custom;

    private void Persist()
    {
        if (_loading) return;
        var s = _settings.Current;

        if (FormatCombo.SelectedItem is FormatItem f) s.OutputFormat = f.Format;
        s.DefaultCaptureMode = SelectedMode();
        s.FrameRate = (int)FpsSlider.Value;
        s.Quality = (int)QualitySlider.Value;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.EnableControlServer = ServerCheck.IsChecked == true;
        if (int.TryParse(PortBox.Text, out var port) && port is > 0 and < 65536)
            s.ControlServerPort = port;

        _settings.Save(s);
        UpdateLibraryUi();
    }

    private void UpdateLibraryUi()
    {
        LibraryPathText.Text = _settings.ResolveLibraryFolder();
        // "Use default" only makes sense when a custom folder is set.
        ResetLibraryButton.IsEnabled = !string.IsNullOrWhiteSpace(_settings.Current.LibraryFolder);
    }

    private void OpenLibrary() => OpenInExplorer(_settings.ResolveLibraryFolder());

    private void ChangeLibrary()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the Snapture library folder",
            InitialDirectory = _settings.ResolveLibraryFolder(),
        };
        if (dlg.ShowDialog(this) == true)
        {
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
