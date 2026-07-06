using Snapture.Core.Capture;
using Snapture.Core.Models;
using Windows.Graphics.Capture;

namespace Snapture.App.Capture;

/// <summary>
/// Chooses the capture backend: Windows.Graphics.Capture when supported (clean
/// GPU frames + correct cursor), falling back to the GDI source otherwise or if
/// WGC initialisation fails on a given target.
/// </summary>
internal static class CaptureBackend
{
    private static readonly bool WgcSupported = CheckWgcSupported();

    public static IFrameSource Create(CaptureTarget target, bool captureCursor)
    {
        if (WgcSupported)
        {
            try { return new WgcFrameSource(target, captureCursor); }
            catch { /* fall through to GDI */ }
        }
        return new GdiFrameSource(target, captureCursor);
    }

    private static bool CheckWgcSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }
}
