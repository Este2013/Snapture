using Snapture.Core.Models;

namespace Snapture.Core.Settings;

/// <summary>
/// User-configurable settings. Serialized to
/// <c>%APPDATA%\Snapture\settings.json</c>. Keep this a plain POCO so it
/// round-trips cleanly through System.Text.Json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Container/codec used when saving.</summary>
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Mp4;

    /// <summary>Capture mode pre-selected when the overlay opens.</summary>
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Custom;

    /// <summary>
    /// Where finished recordings are written. Empty means "use the default
    /// library folder" (Pictures\Snapture), resolved at runtime.
    /// </summary>
    public string LibraryFolder { get; set; } = string.Empty;

    /// <summary>Target capture frame rate (frames per second).</summary>
    public int FrameRate { get; set; } = 30;

    /// <summary>
    /// Quality knob 0-100 mapped per-format (CRF for mp4, quality for webp,
    /// dither/palette for gif). 70 is a good default.
    /// </summary>
    public int Quality { get; set; } = 70;

    /// <summary>Capture the system cursor into the recording.</summary>
    public bool CaptureCursor { get; set; } = true;

    /// <summary>TCP port the Stream Deck control server listens on (localhost).</summary>
    public int ControlServerPort { get; set; } = 48910;

    /// <summary>Start the control server on launch.</summary>
    public bool EnableControlServer { get; set; } = true;

    /// <summary>Open the file location after a recording is saved.</summary>
    public bool RevealAfterSave { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
