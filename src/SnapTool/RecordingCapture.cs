using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SnapTool.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media;

namespace SnapTool;

// Only this adapter knows native device/item handles. No capture surfaces escape a read.
internal sealed class RecordingCapture : IDisposable
{
    private readonly IDirect3DDevice device;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly BitmapBounds crop;
    private readonly RecordingSize size;
    private SoftwareBitmap? latest;
    private volatile bool unavailable;

    internal RecordingCapture(PixelRect region, PixelRect screen, RecordingSize size, IntPtr testWindow = default)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("此系统不支持屏幕录制。");
        this.size = size;
        device = CaptureInterop.CreateDevice();
        try
        {
            item = CaptureInterop.CreateItem(testWindow != IntPtr.Zero ? testWindow :
                CaptureInterop.MonitorFromPoint(new Native.Point { X = region.Left, Y = region.Top }, 2), testWindow != IntPtr.Zero);
            crop = new BitmapBounds { X = (uint)(region.Left - screen.Left), Y = (uint)(region.Top - screen.Top), Width = (uint)region.Width, Height = (uint)region.Height };
            if ((long)crop.X + crop.Width > item.Size.Width || (long)crop.Y + crop.Height > item.Size.Height)
                throw new InvalidOperationException("录屏区域已超出当前显示范围。");
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = true;
                item.Closed += OnClosed;
                try { session.StartCapture(); }
                catch { item.Closed -= OnClosed; session.Dispose(); throw; }
            }
            catch { pool.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args) => unavailable = true;

    internal async Task<SoftwareBitmap> Read(CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (unavailable) throw new InvalidOperationException("录制目标已关闭。");
            Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame != null)
            {
                try
                {
                    while (pool.TryGetNextFrame() is { } newer) { frame.Dispose(); frame = newer; }
                    if (frame.ContentSize.Width != item.Size.Width || frame.ContentSize.Height != item.Size.Height)
                        throw new InvalidOperationException("录制目标大小已改变。");
                    using var source = VideoFrame.CreateWithDirect3D11Surface(frame.Surface);
                    using var destination = new VideoFrame(BitmapPixelFormat.Bgra8, size.Width, size.Height, BitmapAlphaMode.Ignore);
                    await source.CopyToAsync(destination, crop, new BitmapBounds { Width = (uint)size.Width, Height = (uint)size.Height }).AsTask(token).ConfigureAwait(false);
                    latest?.Dispose(); latest = SoftwareBitmap.Copy(destination.SoftwareBitmap);
                }
                finally { frame.Dispose(); }
            }
            if (latest != null) return SoftwareBitmap.Copy(latest);
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("未收到录屏画面，请检查目标是否可见。");
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        item.Closed -= OnClosed; session.Dispose(); pool.Dispose(); latest?.Dispose(); latest = null; device.Dispose();
    }
}

internal static class CaptureInterop
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value, uint length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Native.Point point, uint flags);

    internal static GraphicsCaptureItem CreateItem(IntPtr handle, bool window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out var text));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero; object? wrapper = null;
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out factory));
            wrapper = Marshal.GetObjectForIUnknown(factory);
            var interop = (IGraphicsCaptureItemInterop)wrapper;
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(window ? interop.CreateForWindow(handle, ref itemId, out item) : interop.CreateForMonitor(handle, ref itemId, out item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (wrapper != null) Marshal.ReleaseComObject(wrapper);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }

    internal static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var device, out _, out var context));
        IntPtr dxgi = IntPtr.Zero, projected = IntPtr.Zero;
        try
        {
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != IntPtr.Zero) Marshal.Release(projected);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            Marshal.Release(context); Marshal.Release(device);
        }
    }
}
