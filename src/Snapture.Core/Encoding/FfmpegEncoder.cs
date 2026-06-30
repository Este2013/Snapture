using System.Diagnostics;
using System.Globalization;
using System.Text;
using Snapture.Core.Capture;
using Snapture.Core.Models;

namespace Snapture.Core.Encoding;

/// <summary>
/// Streams raw BGRA frames into a long-running ffmpeg process via stdin and
/// encodes to mp4 / animated webp / gif. One encoder instance per recording.
/// </summary>
public sealed class FfmpegEncoder : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRate;
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private Stream? _stdin;
    private Task? _stderrReader;
    private bool _broken;

    public FfmpegEncoder(string ffmpegPath, int width, int height, int frameRate)
    {
        _ffmpegPath = ffmpegPath;
        _width = width;
        _height = height;
        _frameRate = Math.Clamp(frameRate, 1, 240);
    }

    public string OutputPath { get; private set; } = string.Empty;

    /// <summary>Tail of ffmpeg's stderr, useful for diagnosing failures.</summary>
    public string DiagnosticsLog => _stderr.ToString();

    public void Start(string outputPath, OutputFormat format, int quality)
    {
        OutputPath = outputPath;
        var args = BuildArguments(outputPath, format, quality);

        var psi = new ProcessStartInfo(_ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _stdin = _process.StandardInput.BaseStream;

        // Drain stderr continuously; ffmpeg blocks once the pipe fills otherwise.
        _stderrReader = Task.Run(async () =>
        {
            try
            {
                var reader = _process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (_stderr.Length < 16_000)
                        _stderr.AppendLine(line);
                }
            }
            catch { /* process ended */ }
        });
    }

    /// <summary>Push one frame to the encoder. Returns false if the pipe broke.</summary>
    public bool WriteFrame(Frame frame)
    {
        if (_broken || _stdin is null)
            return false;

        try
        {
            _stdin.Write(frame.Pixels);
            return true;
        }
        catch (IOException)
        {
            _broken = true;
            return false;
        }
        catch (ObjectDisposedException)
        {
            _broken = true;
            return false;
        }
    }

    /// <summary>
    /// Close the input and wait for ffmpeg to finalize the file. Returns true if
    /// ffmpeg exited cleanly and the output file exists.
    /// </summary>
    public async Task<bool> FinishAsync(TimeSpan timeout)
    {
        if (_process is null)
            return false;

        try { _stdin?.Close(); } catch { /* already gone */ }

        var exited = await WaitForExitAsync(_process, timeout).ConfigureAwait(false);
        if (!exited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        if (_stderrReader is not null)
            await Task.WhenAny(_stderrReader, Task.Delay(2000)).ConfigureAwait(false);

        return _process.ExitCode == 0 && File.Exists(OutputPath);
    }

    private string BuildArguments(string outputPath, OutputFormat format, int quality)
    {
        var ci = CultureInfo.InvariantCulture;
        var input =
            $"-f rawvideo -pixel_format bgra " +
            $"-video_size {_width}x{_height} " +
            $"-framerate {_frameRate} -i pipe:0";

        var q = Math.Clamp(quality, 0, 100);

        string output = format switch
        {
            // CRF 0 (lossless) .. 51 (worst); map quality 100->~10, 0->~40.
            OutputFormat.Mp4 =>
                $"-c:v libx264 -pix_fmt yuv420p -preset veryfast " +
                $"-crf {(40 - (int)Math.Round(q * 0.30, MidpointRounding.AwayFromZero)).ToString(ci)} " +
                $"-movflags +faststart",

            // Animated WebP. libwebp quality is 0..100 directly.
            OutputFormat.WebP =>
                $"-c:v libwebp -lossless 0 -compression_level 4 " +
                $"-quality {q.ToString(ci)} -loop 0",

            // Two-stage palette in one graph for good-looking GIFs.
            OutputFormat.Gif =>
                "-filter_complex " +
                "\"[0:v] split [a][b];[a] palettegen=stats_mode=diff [p];" +
                "[b][p] paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\"",

            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        // -y overwrite, -hide_banner quieter logs.
        return $"-y -hide_banner -loglevel warning {input} {output} \"{outputPath}\"";
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                try { _stdin?.Close(); } catch { }
                if (!await WaitForExitAsync(_process, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
