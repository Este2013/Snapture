using System.Diagnostics;
using System.Globalization;
using System.Text;
using Snapture.Core.Capture;
using Snapture.Core.Encoding;
using Snapture.Core.Models;
using Snapture.Core.Settings;

namespace Snapture.Core.Snapshot;

/// <summary>Outcome of a still-image capture.</summary>
public sealed record SnapshotResult(bool Success, string? OutputPath, string? Error);

/// <summary>
/// Captures a single frame of a target region and encodes it to a still image
/// (PNG/JPEG/WebP) with a one-shot ffmpeg invocation. The video equivalent is
/// <see cref="Recording.RecordingController"/>; this shares the same
/// <see cref="IFrameSource"/> capture path so display/window/custom targets
/// behave identically.
/// </summary>
public sealed class SnapshotService
{
    private readonly SettingsService _settings;
    private readonly Func<CaptureTarget, bool, IFrameSource> _frameSourceFactory;

    public SnapshotService(
        SettingsService settings,
        Func<CaptureTarget, bool, IFrameSource>? frameSourceFactory = null)
    {
        _settings = settings;
        _frameSourceFactory = frameSourceFactory ?? ((t, c) => new GdiFrameSource(t, c));
    }

    public async Task<SnapshotResult> CaptureAsync(CaptureTarget target)
    {
        var ffmpeg = FfmpegLocator.Resolve();
        if (ffmpeg is null)
            return new SnapshotResult(false, null,
                "ffmpeg was not found. Bundle ffmpeg.exe next to the app or add it to PATH.");

        var s = _settings.Current;
        var region = target.Region.ToEvenDimensions();
        if (region.IsEmpty)
            return new SnapshotResult(false, null, "Capture region is too small.");

        byte[] pixels;
        int width, height;
        using (var source = _frameSourceFactory(target with { Region = region }, s.SnapshotCaptureCursor))
        {
            width = source.Width;
            height = source.Height;
            using var frame = source.Capture(TimeSpan.Zero);
            if (frame is null)
                return new SnapshotResult(false, null, "Failed to capture the screen.");
            pixels = frame.Pixels.ToArray();
        }

        var outputPath = BuildOutputPath(s.SnapshotFormat);
        try
        {
            await EncodeAsync(ffmpeg, pixels, width, height, s.SnapshotFormat, s.Quality, outputPath)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SnapshotResult(false, null, ex.Message);
        }

        return File.Exists(outputPath)
            ? new SnapshotResult(true, outputPath, null)
            : new SnapshotResult(false, null, "ffmpeg did not produce an output file.");
    }

    private static async Task EncodeAsync(string ffmpeg, byte[] bgra, int width, int height,
        ImageFormat format, int quality, string outputPath)
    {
        var ci = CultureInfo.InvariantCulture;
        var q = Math.Clamp(quality, 0, 100);

        // GDI leaves alpha at 0, so force opaque pixel formats to avoid a fully
        // transparent image.
        string codec = format switch
        {
            ImageFormat.Png => "-pix_fmt rgb24",
            // mjpeg -q:v is 2 (best) .. 31 (worst); map quality 100->2, 0->~31.
            ImageFormat.Jpeg =>
                $"-c:v mjpeg -pix_fmt yuvj420p -q:v {(2 + (int)Math.Round((100 - q) * 0.29)).ToString(ci)}",
            ImageFormat.WebP =>
                $"-c:v libwebp -pix_fmt yuv420p -lossless 0 -quality {q.ToString(ci)}",
            _ => "-pix_fmt rgb24",
        };

        var args =
            $"-y -hide_banner -loglevel warning -f rawvideo -pixel_format bgra " +
            $"-video_size {width}x{height} -i pipe:0 -frames:v 1 {codec} \"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stderr = new StringBuilder();
        var errReader = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    if (stderr.Length < 8000) stderr.AppendLine(line);
            }
            catch { /* process ended */ }
        });

        try
        {
            await proc.StandardInput.BaseStream.WriteAsync(bgra).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch { /* pipe may close early on failure; exit code reports it */ }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ffmpeg timed out encoding the snapshot.");
        }

        await Task.WhenAny(errReader, Task.Delay(1000)).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg failed: " + Tail(stderr.ToString()));
    }

    private string BuildOutputPath(ImageFormat format)
    {
        var folder = _settings.ResolveSnapshotLibraryFolder();
        var ext = format switch
        {
            ImageFormat.Png => "png",
            ImageFormat.Jpeg => "jpg",
            ImageFormat.WebP => "webp",
            _ => "png",
        };
        var name = $"Snapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}";
        return Path.Combine(folder, name);
    }

    private static string Tail(string s, int max = 300) => s.Length <= max ? s : s[^max..];
}
