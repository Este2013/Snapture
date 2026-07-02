using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Snapture.App;
using Snapture.Core.Update;

namespace Snapture.App.Views;

/// <summary>
/// Modal showing a release's notes. When constructed with an update target it
/// also offers an Install button that downloads the installer, launches it, and
/// exits the app so the running files can be replaced.
/// </summary>
public partial class ReleaseNotesWindow : Window
{
    private readonly UpdateService? _updater;
    private readonly ReleaseInfo? _release;

    public ReleaseNotesWindow(string title, string notes, UpdateService? updater = null, ReleaseInfo? release = null,
        string? subtitle = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            SubtitleText.Text = subtitle;
            SubtitleText.Visibility = Visibility.Visible;
        }
        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? "No release notes were provided." : notes;

        _updater = updater;
        _release = release;
        if (updater is null || release is null)
            InstallButton.Visibility = Visibility.Collapsed;

        HeaderBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        HeaderClose.Click += (_, _) => Close();
        CloseButton.Click += (_, _) => Close();
        InstallButton.Click += async (_, _) => await InstallAsync();
    }

    private async Task InstallAsync()
    {
        if (_updater is null || _release is null) return;

        InstallButton.IsEnabled = false;
        HeaderClose.IsEnabled = false;
        CloseButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading…";

        var progress = new Progress<double>(p => DownloadProgress.Value = p * 100);
        try
        {
            var path = await _updater.DownloadInstallerAsync(_release, progress);
            StatusText.Text = "Launching installer…";
            // Silent install (Inno shows just a progress window, then relaunches
            // Snapture). ShellExecute so it can elevate if it ever needs to.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = "/SILENT" });
            // Let the app actually exit (the settings window normally hides to tray).
            if (Owner is MainWindow main) main.AllowClose = true;
            Application.Current.Shutdown(); // release file locks for the installer
        }
        catch (Exception ex)
        {
            StatusText.Text = "Update failed: " + ex.Message;
            InstallButton.IsEnabled = true;
            HeaderClose.IsEnabled = true;
            CloseButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }
}
