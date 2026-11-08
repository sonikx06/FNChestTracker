using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ChestTracker.Core.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ChestTracker.App.Capture;

/// <summary>
/// Frame source based on the public Windows.Graphics.Capture API (the same API used by the
/// Windows Snipping Tool and OBS "Windows 10" capture). It receives composed window pixels from
/// the OS compositor; it does not touch the game process. Frames arriving faster than the
/// sampling rate are released immediately without being copied.
/// </summary>
public sealed class WgcCaptureSource : ICaptureSource
{
    private readonly object _gate = new();
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _size;
    private long _intervalTicks;
    private long _nextFrameAt;
    private int _busy;
    private byte[] _buffer = [];

    public string Name => "Windows.Graphics.Capture";

    public bool IsRunning { get; private set; }

    public event EventHandler<CapturedFrame>? FrameCaptured;

    public event EventHandler<string>? Failed;

    public static bool IsSupported()
    {
        try
        {
            return ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8)
                   && GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public void Start(CaptureTarget target, double framesPerSecond)
    {
        lock (_gate)
        {
            StopCore();
            _intervalTicks = (long)(Stopwatch.Frequency / Math.Clamp(framesPerSecond, 0.5, 30));
            _device = Direct3D11Helper.CreateDevice();
            _item = target.WindowHandle != IntPtr.Zero
                ? CaptureItemInterop.ForWindow(target.WindowHandle)
                : CaptureItemInterop.ForMonitor(target.MonitorHandle);
            _item.Closed += OnItemClosed;
            _size = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);

            TrySetSessionOptions(_session);
            _session.StartCapture();
            IsRunning = true;
            Log.Info($"WGC capture started: {target.Description} {_size.Width}x{_size.Height}");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private static void TrySetSessionOptions(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
            {
                session.IsCursorCaptureEnabled = false;
            }
        }
        catch
        {
            // Optional.
        }

        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                // Windows 11: hide the yellow capture border where the OS allows it.
                session.IsBorderRequired = false;
            }
        }
        catch
        {
            // Not allowed for this app; the border stays visible.
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Stop();
        Failed?.Invoke(this, "Capture target closed");
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                lock (_gate)
                {
                    if (_device is not null && _pool is not null)
                    {
                        _size = frame.ContentSize;
                        _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    }
                }

                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (now < Interlocked.Read(ref _nextFrameAt) || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _nextFrameAt, now + _intervalTicks);
            try
            {
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
                frame.Dispose();
                frame = null;

                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var length = width * height * 4;
                if (_buffer.Length != length)
                {
                    _buffer = new byte[length];
                }

                bitmap.CopyToBuffer(_buffer.AsBuffer());
                FrameCaptured?.Invoke(this, new CapturedFrame
                {
                    Pixels = _buffer, Width = width, Height = height, Stride = width * 4, Timestamp = DateTimeOffset.UtcNow,
                });
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WGC frame processing failed", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void StopCore()
    {
        IsRunning = false;
        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
        }

        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        try
        {
            _session?.Dispose();
            _pool?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("WGC cleanup failed", ex);
        }

        (_device as IDisposable)?.Dispose();
        _session = null;
        _pool = null;
        _item = null;
        _device = null;
    }

    public void Dispose() => Stop();
}

internal static class CaptureItemInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    public static GraphicsCaptureItem ForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public static GraphicsCaptureItem ForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }
}

internal static class Direct3D11Helper
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out var d3dDevice, out _, out var context);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out d3dDevice, out _, out context);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, ref iid, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(d3dDevice);
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }
        }
    }
}
