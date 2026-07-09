namespace Snapture.Core.Models;

/// <summary>Output container/codec the recording is saved as.</summary>
public enum OutputFormat
{
    Mp4,
    Gif,
    WebP,
}

/// <summary>Still-image format a snapshot is saved as.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>Whether a capture produces a video recording or a still snapshot.</summary>
public enum CaptureKind
{
    Video,
    Image,
}

/// <summary>How the capture area is chosen.</summary>
public enum CaptureMode
{
    /// <summary>Capture an entire display.</summary>
    Display,

    /// <summary>Capture a single application window.</summary>
    Window,

    /// <summary>Capture a user-drawn rectangle.</summary>
    Custom,
}

/// <summary>
/// Where the picker's toolbar is anchored on the active monitor. The four
/// edge-centre positions render the bar vertically; corners render it horizontally.
/// </summary>
public enum PickerBarPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    RightCenter,
    BottomRight,
    BottomCenter,
    BottomLeft,
    LeftCenter,
}

/// <summary>
/// Which edge of the selection the mini-toolbar attaches to (rendering inside
/// that edge when there's no room outside), or docked to the main toolbar.
/// </summary>
public enum SelectionToolbarPlacement
{
    Left,
    Right,
    Top,
    Bottom,
    DockToMain,
}

/// <summary>Lifecycle of the recording pipeline. Drives both the UI and IPC.</summary>
public enum RecordingState
{
    /// <summary>Nothing happening; tray is idle.</summary>
    Idle,

    /// <summary>Overlay is up; the user is choosing the capture area.</summary>
    Selecting,

    /// <summary>Frames are being captured and encoded.</summary>
    Recording,

    /// <summary>Capture stopped; encoder is flushing/finalizing the file.</summary>
    Encoding,
}
