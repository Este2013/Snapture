using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Snapture.App;

/// <summary>
/// Returns memory to the OS once the app is idle. Capture bursts allocate large,
/// short-lived buffers (frames, full-desktop overlay surfaces, GPU staging
/// textures); .NET keeps that in the working set long after it's free. A tray
/// utility shouldn't sit on hundreds of MB in the background, so after a capture
/// we compact the heap and trim the working set.
/// </summary>
internal static class MemoryHygiene
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);

    public static void Trim()
    {
        try
        {
            // Large frames land on the LOH; compact it so the freed space is
            // actually released rather than left as fragmented reserved heap.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers(); // run finalizers freeing native window/GPU surfaces
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { /* best effort */ }
    }
}
