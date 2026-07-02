using System.Diagnostics;

namespace Snapture.Core.Encoding;

/// <summary>
/// Makes a small PNG thumbnail from any capture (image or video) via a one-shot
/// ffmpeg call — used as the image on the "saved" toast notification. For videos
/// this grabs the first frame; Windows can't thumbnail a video on its own.
/// </summary>
public static class Thumbnailer
{
    /// <summary>Create a scaled PNG thumbnail in %TEMP%, or null if it couldn't be made.</summary>
    public static async Task<string?> CreateAsync(string sourcePath, int width = 360)
    {
        var ffmpeg = FfmpegLocator.Resolve();
        if (ffmpeg is null || !File.Exists(sourcePath))
            return null;

        var outPath = Path.Combine(Path.GetTempPath(), $"snapture_thumb_{Guid.NewGuid():N}.png");
        var args = $"-y -hide_banner -loglevel error -i \"{sourcePath}\" " +
                   $"-frames:v 1 -vf \"scale={width}:-1\" \"{outPath}\"";

        try
        {
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return File.Exists(outPath) ? outPath : null;
        }
        catch
        {
            return null;
        }
    }
}
