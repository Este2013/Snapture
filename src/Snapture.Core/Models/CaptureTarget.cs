namespace Snapture.Core.Models;

/// <summary>
/// Fully resolved description of what to record, produced by the overlay (or the
/// IPC layer) and handed to the <c>RecordingController</c>. Whatever the mode,
/// it always resolves down to a physical-pixel <see cref="CaptureRegion"/>.
/// </summary>
public sealed record CaptureTarget
{
    public required CaptureMode Mode { get; init; }

    /// <summary>The pixel rectangle to record.</summary>
    public required CaptureRegion Region { get; init; }

    /// <summary>
    /// For <see cref="CaptureMode.Window"/>, the source window handle so the
    /// capture can follow/validate the window. Zero otherwise.
    /// </summary>
    public nint WindowHandle { get; init; }

    /// <summary>Human-readable label for logs / IPC events (e.g. monitor name).</summary>
    public string? Label { get; init; }
}
