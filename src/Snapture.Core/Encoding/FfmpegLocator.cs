using System.Diagnostics;

namespace Snapture.Core.Encoding;

/// <summary>
/// Finds the ffmpeg executable. Preference order: an explicitly configured path,
/// then a copy bundled next to the app (<c>ffmpeg\ffmpeg.exe</c> or
/// <c>ffmpeg.exe</c> in the app dir), then whatever is on PATH.
/// </summary>
public static class FfmpegLocator
{
    public static string? Resolve(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                     Path.Combine(baseDir, "ffmpeg.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
    }

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full))
                    return full;
            }
            catch { /* ignore malformed PATH entries */ }
        }

        return null;
    }

    /// <summary>True if ffmpeg can be located and launched.</summary>
    public static bool Verify(string? configuredPath, out string? resolvedPath)
    {
        resolvedPath = Resolve(configuredPath);
        if (resolvedPath is null)
            return false;

        try
        {
            using var p = Process.Start(new ProcessStartInfo(resolvedPath, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null)
                return false;
            p.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
