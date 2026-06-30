using System.Windows;
using Snapture.App.Interop;

namespace Snapture.App.Views;

/// <summary>
/// Compact always-on-top bar shown while recording: a blinking indicator, the
/// elapsed time, and a Stop button. Lives independently of the selection
/// overlay so the rest of the desktop is fully usable during capture.
/// </summary>
public partial class RecordingBarWindow : Window
{
    public RecordingBarWindow()
    {
        InitializeComponent();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
        Loaded += (_, _) =>
        {
            NativeMethods.MarkToolWindow(this, noActivate: true);
            PositionTopCenter();
        };
        SizeChanged += (_, _) => PositionTopCenter();
    }

    public event Action? StopRequested;

    public void UpdateElapsed(TimeSpan elapsed) =>
        ElapsedText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

    private void PositionTopCenter()
    {
        var screenW = SystemParameters.PrimaryScreenWidth;
        Left = Math.Max(0, (screenW - ActualWidth) / 2);
        Top = 12;
    }
}
