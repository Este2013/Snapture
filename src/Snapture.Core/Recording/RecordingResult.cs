namespace Snapture.Core.Recording;

/// <summary>Outcome of a finished recording.</summary>
public sealed record RecordingResult(
    bool Success,
    string? OutputPath,
    int FrameCount,
    TimeSpan Duration,
    string? Error);
