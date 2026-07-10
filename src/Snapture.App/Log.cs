using System.IO;

namespace Snapture.App;

/// <summary>
/// Minimal file logger at <c>%APPDATA%\Snapture\snapture.log</c>. The previous
/// session is kept as <c>.1</c> so a crash's log survives one relaunch. Cheap,
/// lock-guarded, and swallows its own errors so logging can never break the app.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string FilePath => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snapture");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "snapture.log");
    }

    public static void StartSession(string version)
    {
        try
        {
            var f = FilePath;
            if (File.Exists(f))
            {
                var prev = f + ".1";
                try { File.Delete(prev); } catch { }
                try { File.Move(f, prev); } catch { }
            }
            Write($"=== Snapture {version} session started (PID {Environment.ProcessId}) ===");
            Write($"OS {Environment.OSVersion}; {Environment.ProcessorCount} cores; 64-bit={Environment.Is64BitProcess}");
        }
        catch { }
    }

    public static void Info(string message) => Write(message);

    public static void Error(string context, Exception ex) => Write($"ERROR [{context}] {ex}");

    private static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
