using System.Buffers;

namespace Snapture.Core.Capture;

/// <summary>
/// A single captured frame: tightly-packed 32-bit BGRA pixels (the layout
/// ffmpeg expects for <c>-pix_fmt bgra</c>). The backing buffer is rented from a
/// pool; callers must <see cref="Dispose"/> the frame to return it.
/// </summary>
public sealed class Frame : IDisposable
{
    private byte[]? _buffer;

    private Frame(byte[] buffer, int width, int height, int stride, TimeSpan timestamp)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        Timestamp = timestamp;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row, including any padding (usually width * 4).</summary>
    public int Stride { get; }

    /// <summary>Time since recording started.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Valid pixel data span (length = Stride * Height).</summary>
    public ReadOnlySpan<byte> Pixels =>
        _buffer is null
            ? throw new ObjectDisposedException(nameof(Frame))
            : _buffer.AsSpan(0, Stride * Height);

    /// <summary>Rent a pooled frame of the given dimensions (BGRA, no padding).</summary>
    public static Frame Rent(int width, int height, TimeSpan timestamp)
    {
        var stride = width * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(stride * height);
        return new Frame(buffer, width, height, stride, timestamp);
    }

    /// <summary>Mutable destination span for filling the frame.</summary>
    public Span<byte> GetWritableSpan() =>
        _buffer is null
            ? throw new ObjectDisposedException(nameof(Frame))
            : _buffer.AsSpan(0, Stride * Height);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
