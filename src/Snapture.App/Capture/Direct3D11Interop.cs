using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Snapture.App.Capture;

/// <summary>
/// Native glue for the Windows.Graphics.Capture backend: bridging a Direct3D 11
/// device to the WinRT <see cref="IDirect3DDevice"/>, obtaining the DXGI texture
/// behind a capture frame's surface, and creating a <see cref="GraphicsCaptureItem"/>
/// for a monitor via the COM interop factory. Kept version-agnostic by going
/// through classic COM interop (RoGetActivationFactory / GetObjectForIUnknown)
/// rather than CsWinRT internals.
/// </summary>
internal static class Direct3D11Interop
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    // IID of IGraphicsCaptureItem (the interface CreateForMonitor returns).
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    // IID of ID3D11Texture2D.
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    /// <summary>Wrap a native DXGI device pointer as a WinRT <see cref="IDirect3DDevice"/>.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(nint dxgiDevice)
    {
        uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDevice);
        if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed (0x{hr:X8}).");
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            Marshal.Release(graphicsDevice);
        }
    }

    /// <summary>Get the native ID3D11Texture2D pointer behind a capture frame surface.</summary>
    public static nint GetDxgiTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = IID_ID3D11Texture2D;
        return access.GetInterface(ref iid);
    }

    /// <summary>Create a capture item for a monitor handle (HMONITOR).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hmon)
    {
        var interop = GetInteropFactory();
        var iid = IID_IGraphicsCaptureItem;
        nint itemPtr = interop.CreateForMonitor(hmon, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != nint.Zero) Marshal.Release(itemPtr);
        }
    }

    private static IGraphicsCaptureItemInterop GetInteropFactory()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hstr);
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            int hr = RoGetActivationFactory(hstr, ref iid, out var factoryPtr);
            if (hr != 0 || factoryPtr == nint.Zero)
                throw new InvalidOperationException($"RoGetActivationFactory failed (0x{hr:X8}).");
            try
            {
                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }
}
