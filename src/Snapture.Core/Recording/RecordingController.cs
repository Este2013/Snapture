using System.Diagnostics;
using Snapture.Core.Capture;
using Snapture.Core.Encoding;
using Snapture.Core.Models;
using Snapture.Core.Settings;

namespace Snapture.Core.Recording;

/// <summary>
/// The orchestrator. Owns the <see cref="RecordingState"/> machine and runs the
/// capture loop on a dedicated thread, feeding frames to <see cref="FfmpegEncoder"/>.
/// Both the WPF UI and the Stream Deck IPC layer drive recording exclusively
/// through this type, so the behaviour is identical regardless of trigger.
///
/// State transitions:
///   Idle --BeginSelection--> Selecting --StartAsync--> Recording
///   Recording --StopAsync--> Encoding --> Idle
///   (Selecting|Recording|Encoding) --Abort--> Idle
/// </summary>
public sealed class RecordingController : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<CaptureTarget, bool, IFrameSource> _frameSourceFactory;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private FfmpegEncoder? _encoder;
    private volatile int _frameCount;
    private TimeSpan _duration;

    public RecordingController(
        SettingsService settings,
        Func<CaptureTarget, bool, IFrameSource>? frameSourceFactory = null)
    {
        _settings = settings;
        _frameSourceFactory = frameSourceFactory
            ?? ((target, cursor) => new GdiFrameSource(target, cursor));
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>Raised (on a background thread) when a recording finishes.</summary>
    public event EventHandler<RecordingResult>? RecordingCompleted;

    /// <summary>Move into selection mode (UI shows the overlay).</summary>
    public bool BeginSelection()
    {
        lock (_gate)
        {
            if (State != RecordingState.Idle)
                return false;
            Transition(RecordingState.Selecting);
            return true;
        }
    }

    /// <summary>
    /// Begin recording the resolved target. Valid from Selecting (normal flow)
    /// or directly from Idle (e.g. an IPC "instant" trigger).
    /// </summary>
    public void StartAsync(CaptureTarget target)
    {
        lock (_gate)
        {
            if (State is not (RecordingState.Selecting or RecordingState.Idle))
                throw new InvalidOperationException($"Cannot start recording from {State}.");

            var ffmpeg = FfmpegLocator.Resolve()
                ?? throw new InvalidOperationException(
                    "ffmpeg was not found. Bundle ffmpeg.exe next to the app or add it to PATH.");

            var s = _settings.Current;
            var region = target.Region.ToEvenDimensions();
            if (region.IsEmpty)
                throw new InvalidOperationException("Capture region is too small.");

            var source = _frameSourceFactory(target with { Region = region }, s.CaptureCursor);
            var encoder = new FfmpegEncoder(ffmpeg, source.Width, source.Height, s.FrameRate);
            var outputPath = BuildOutputPath(s.OutputFormat);
            encoder.Start(outputPath, s.OutputFormat, s.Quality);

            _encoder = encoder;
            _frameCount = 0;
            _cts = new CancellationTokenSource();
            _captureThread = new Thread(() => CaptureLoop(source, encoder, s.FrameRate, _cts.Token))
            {
                IsBackground = true,
                Name = "Snapture.Capture",
                Priority = ThreadPriority.AboveNormal,
            };

            Transition(RecordingState.Recording);
            _captureThread.Start();
        }
    }

    /// <summary>Stop recording and finalize the file. Safe to call once.</summary>
    public async Task<RecordingResult> StopAsync()
    {
        Thread? thread;
        FfmpegEncoder? encoder;
        lock (_gate)
        {
            if (State != RecordingState.Recording)
                return new RecordingResult(false, null, 0, TimeSpan.Zero, "Not recording.");

            _cts?.Cancel();
            thread = _captureThread;
            encoder = _encoder;
            Transition(RecordingState.Encoding);
        }

        // Let the capture loop drain and dispose the frame source.
        thread?.Join(TimeSpan.FromSeconds(5));

        RecordingResult result;
        if (encoder is null)
        {
            result = new RecordingResult(false, null, 0, TimeSpan.Zero, "Encoder missing.");
        }
        else
        {
            var ok = await encoder.FinishAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            result = new RecordingResult(
                ok, ok ? encoder.OutputPath : null, _frameCount, _duration,
                ok ? null : "ffmpeg failed: " + Tail(encoder.DiagnosticsLog));
            await encoder.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            _encoder = null;
            _captureThread = null;
            _cts?.Dispose();
            _cts = null;
            Transition(RecordingState.Idle);
        }

        RecordingCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>Abort from any active state and return to Idle, discarding output.</summary>
    public async Task AbortAsync()
    {
        Thread? thread;
        FfmpegEncoder? encoder;
        string? partialPath;
        lock (_gate)
        {
            if (State == RecordingState.Idle)
                return;
            _cts?.Cancel();
            thread = _captureThread;
            encoder = _encoder;
            partialPath = encoder?.OutputPath;
        }

        thread?.Join(TimeSpan.FromSeconds(5));
        if (encoder is not null)
            await encoder.DisposeAsync().ConfigureAwait(false);

        // Discard any partial file.
        if (!string.IsNullOrEmpty(partialPath))
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
        }

        lock (_gate)
        {
            _encoder = null;
            _captureThread = null;
            _cts?.Dispose();
            _cts = null;
            Transition(RecordingState.Idle);
        }
    }

    private void CaptureLoop(IFrameSource source, FfmpegEncoder encoder, int fps, CancellationToken token)
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 240));
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = clock.Elapsed;
                if (now < next)
                {
                    var wait = next - now;
                    // Sleep most of the gap, then spin briefly for accuracy.
                    if (wait > TimeSpan.FromMilliseconds(2))
                        Thread.Sleep(wait - TimeSpan.FromMilliseconds(1));
                    continue;
                }

                using (var frame = source.Capture(now))
                {
                    if (frame is not null)
                    {
                        if (!encoder.WriteFrame(frame))
                            break; // pipe broke; stop feeding
                        _frameCount++;
                    }
                }

                next += frameInterval;
                // If we've fallen far behind, resync to avoid a burst of frames.
                if (clock.Elapsed - next > frameInterval * 4)
                    next = clock.Elapsed;
            }
        }
        catch
        {
            // Swallow; StopAsync/AbortAsync report the outcome via the encoder.
        }
        finally
        {
            _duration = clock.Elapsed;
            source.Dispose();
        }
    }

    private string BuildOutputPath(OutputFormat format)
    {
        var folder = _settings.ResolveLibraryFolder();
        var ext = format switch
        {
            OutputFormat.Mp4 => "mp4",
            OutputFormat.Gif => "gif",
            OutputFormat.WebP => "webp",
            _ => "mp4",
        };
        var name = $"Snapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}";
        return Path.Combine(folder, name);
    }

    private void Transition(RecordingState next)
    {
        var old = State;
        if (old == next)
            return;
        State = next;
        StateChanged?.Invoke(this, new StateChangedEventArgs(old, next));
    }

    private static string Tail(string s, int max = 400) =>
        s.Length <= max ? s : s[^max..];

    public async ValueTask DisposeAsync()
    {
        await AbortAsync().ConfigureAwait(false);
    }
}
