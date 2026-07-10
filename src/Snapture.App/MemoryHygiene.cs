using System.Runtime;

namespace Snapture.App;

/// <summary>
/// Gently reclaims managed memory once the app is idle. Capture bursts put large,
/// short-lived buffers on the Large Object Heap; compacting it once returns that
/// space to the OS. Deliberately does NOT trim the process working set
/// (EmptyWorkingSet / SetProcessWorkingSetSize): paging out the WPF composition
/// thread's GPU surfaces crashes the render thread (UCEERR_RENDERTHREADFAILURE)
/// and can reset the display.
/// </summary>
internal static class MemoryHygiene
{
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers(); // let finalizers free native window/GPU surfaces
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { /* best effort */ }
    }
}
