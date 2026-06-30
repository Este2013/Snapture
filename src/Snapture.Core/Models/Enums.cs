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
