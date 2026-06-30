using Snapture.Core.Models;

namespace Snapture.Core.Capture;

/// <summary>
/// Produces frames for a given <see cref="CaptureTarget"/>. This is the seam
/// that isolates the recording pipeline from the capture backend. The current
/// implementation is GDI-based (<see cref="GdiFrameSource"/>); a
/// Windows.Graphics.Capture backend can be added later without touching
/// <c>FfmpegEncoder</c> or <c>RecordingController</c>.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>Fixed output dimensions (even-sized) of every produced frame.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Capture one frame at <paramref name="timestamp"/>. Returns null if a
    /// frame could not be grabbed (e.g. the source window vanished); the caller
    /// decides whether to retry or stop.
    /// </summary>
    Frame? Capture(TimeSpan timestamp);
}
