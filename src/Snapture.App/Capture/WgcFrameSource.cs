using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Snapture.Core.Capture;
using Snapture.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Snapture.App.Capture;

/// <summary>
/// Windows.Graphics.Capture frame source: captures the monitor containing the
/// target region via the DWM's own frame pool (clean GPU frames, correct cursor
/// compositing incl. animated .ani cursors) and crops the region out on the CPU.
/// Replaces the GDI path's issues with hardware-composited surfaces and alpha
/// cursors. Requires Windows 10 2004 (build 19041)+.
/// </summary>
internal sealed class WgcFrameSource : IFrameSource
{
    private readonly int _cropX, _cropY;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _rtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private byte[]? _latest;   // tight BGRA crop, Width*4*Height
    private volatile bool _haveFrame;
    private bool _disposed;
    private readonly ManualResetEventSlim _firstFrame = new(false);

    // The free-threaded pool fires at the monitor's refresh rate; cap the (costly)
    // GPU->CPU readback so a 144/240 Hz panel doesn't melt the CPU. Frames are
    // still drained every time so the pool keeps flowing.
    private readonly System.Diagnostics.Stopwatch _copyClock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly TimeSpan MinCopyInterval = TimeSpan.FromMilliseconds(8); // ~120 Hz ceiling
    private TimeSpan _nextCopyAt = TimeSpan.Zero;

    public WgcFrameSource(CaptureTarget target, bool captureCursor)
    {
        var region = target.Region.ToEvenDimensions();
        if (region.IsEmpty)
            throw new ArgumentException("Capture region is empty.", nameof(target));
        Width = region.Width;
        Height = region.Height;

        // Find the monitor holding the region's top-left and its physical bounds.
        var pt = new POINT { X = region.X, Y = region.Y };
        nint hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi))
            throw new InvalidOperationException("Could not resolve the monitor for capture.");
        _cropX = region.X - mi.rcMonitor.left;
        _cropY = region.Y - mi.rcMonitor.top;

        // D3D11 device (BGRA support required by the capture frame pool).
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out _device, out _context);
        if (res.Failure)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                levels, out _device, out _context).CheckError();

        using (var dxgi = _device!.QueryInterface<IDXGIDevice>())
            _rtDevice = Direct3D11Interop.CreateDirect3DDevice(dxgi.NativePointer);

        var item = Direct3D11Interop.CreateItemForMonitor(hmon);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _session = _framePool.CreateCaptureSession(item);
        try { _session.IsCursorCaptureEnabled = captureCursor; } catch { /* older builds */ }

        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public int Width { get; }
    public int Height { get; }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // Recycle every frame, but only pay for the readback at our ceiling
            // (always take the first frame so recording/snapshots start promptly).
            if (_haveFrame && _copyClock.Elapsed < _nextCopyAt) return;
            _nextCopyAt = _copyClock.Elapsed + MinCopyInterval;

            nint texPtr = Direct3D11Interop.GetDxgiTexture(frame.Surface);
            if (texPtr == nint.Zero) return;
            using var tex = new ID3D11Texture2D(texPtr);
            var desc = tex.Description;

            int texW = (int)desc.Width, texH = (int)desc.Height;
            EnsureStaging(texW, texH);
            _context!.CopyResource(_staging!, tex);

            var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                _latest ??= new byte[Width * 4 * Height];
                int dstStride = Width * 4;
                int rowPitch = (int)map.RowPitch;
                for (int y = 0; y < Height; y++)
                {
                    int sy = _cropY + y;
                    if (sy < 0 || sy >= texH) continue;
                    // Clamp the copied span to the available width.
                    int copyBytes = Math.Min(dstStride, Math.Max(0, texW - _cropX) * 4);
                    if (copyBytes <= 0 || _cropX < 0) continue;
                    nint srcRow = map.DataPointer + sy * rowPitch + _cropX * 4;
                    Marshal.Copy(srcRow, _latest, y * dstStride, copyBytes);
                }
                _haveFrame = true;
                _firstFrame.Set();
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
    }

    private void EnsureStaging(int w, int h)
    {
        if (_staging is not null && _staging.Description.Width == (uint)w && _staging.Description.Height == (uint)h)
            return;
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    public Frame? Capture(TimeSpan timestamp)
    {
        // The first frame arrives asynchronously; a single-shot snapshot would
        // otherwise see nothing. Wait briefly (outside the lock) for it.
        if (!_haveFrame && !_disposed)
            _firstFrame.Wait(1500);

        lock (_gate)
        {
            if (_disposed || !_haveFrame || _latest is null)
                return null;
            var frame = Frame.Rent(Width, Height, timestamp);
            _latest.AsSpan(0, Width * 4 * Height).CopyTo(frame.GetWritableSpan());
            return frame;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { (_rtDevice as IDisposable)?.Dispose(); } catch { }
        try { _firstFrame.Dispose(); } catch { }
    }

    // ---- monitor lookup --------------------------------------------------

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);
}
