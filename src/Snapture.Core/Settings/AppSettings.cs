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
    /// library folder" (Videos\Snapture), resolved at runtime.
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

    // ---- Snapshot (still image) settings ---------------------------------

    /// <summary>Still-image format used when saving a snapshot.</summary>
    public ImageFormat SnapshotFormat { get; set; } = ImageFormat.Png;

    /// <summary>Capture mode pre-selected when the overlay opens for a snapshot.</summary>
    public CaptureMode SnapshotCaptureMode { get; set; } = CaptureMode.Display;

    /// <summary>Capture the system cursor into the snapshot.</summary>
    public bool SnapshotCaptureCursor { get; set; } = true;

    /// <summary>Copy the snapshot to the system clipboard as soon as it's taken.</summary>
    public bool SnapshotToClipboard { get; set; } = true;

    /// <summary>
    /// Where snapshots are written. Empty means the default (Pictures\Snapture),
    /// resolved at runtime.
    /// </summary>
    public string SnapshotLibraryFolder { get; set; } = string.Empty;

    /// <summary>TCP port the Stream Deck control server listens on (localhost).</summary>
    public int ControlServerPort { get; set; } = 48910;

    /// <summary>Start the control server on launch.</summary>
    public bool EnableControlServer { get; set; } = true;

    // ---- General ---------------------------------------------------------

    /// <summary>Which capture kind the app defaults to: "lastused", "image", or "video".</summary>
    public string DefaultSnapKind { get; set; } = "lastused";

    /// <summary>The last capture kind used ("image" or "video"), for "Last used".</summary>
    public string LastUsedSnapKind { get; set; } = "video";

    /// <summary>Master switch for the global hotkeys.</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>Global hotkey for taking a snapshot (default F6).</summary>
    public HotkeyBinding SnapshotHotkey { get; set; } = new() { VirtualKey = 0x75, Display = "F6" };

    /// <summary>Global hotkey for recording / stopping (default F7).</summary>
    public HotkeyBinding RecordHotkey { get; set; } = new() { VirtualKey = 0x76, Display = "F7" };

    /// <summary>Open the file location after a recording is saved.</summary>
    public bool RevealAfterSave { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>A configurable global hotkey: a Win32 modifier mask + virtual-key code.</summary>
public sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;

    /// <summary>Win32 MOD_* mask (ALT=1, CONTROL=2, SHIFT=4, WIN=8); 0 = no modifier.</summary>
    public int Modifiers { get; set; }

    /// <summary>Virtual-key code (e.g. 0x75 = F6).</summary>
    public int VirtualKey { get; set; }

    /// <summary>Human-readable label, e.g. "Ctrl+Alt+F6".</summary>
    public string Display { get; set; } = string.Empty;
}
