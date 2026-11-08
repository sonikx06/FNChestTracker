using System.Runtime.InteropServices;
using ChestTracker.App.Interop;
using ChestTracker.Core.Diagnostics;
using Drawing = System.Drawing;

namespace ChestTracker.App.Capture;

/// <summary>
/// Fallback source: copies the target's screen rectangle from the desktop with GDI.
/// Works for borderless-windowed games on most systems; exclusive fullscreen usually yields black frames.
/// </summary>
public sealed class GdiCaptureSource : ICaptureSource
{
    private readonly object _gate = new();
    private Timer? _timer;
    private CaptureTarget? _target;
    private byte[] _buffer = [];
    private int _busy;

    public string Name => "GDI";

    public bool IsRunning { get; private set; }

    public event EventHandler<CapturedFrame>? FrameCaptured;

    public event EventHandler<string>? Failed;

    public void Start(CaptureTarget target, double framesPerSecond)
    {
        lock (_gate)
        {
            Stop();
            _target = target;
            var period = TimeSpan.FromSeconds(1 / Math.Clamp(framesPerSecond, 0.5, 30));
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, period);
            IsRunning = true;
            Log.Info($"GDI capture started: {target.Description}");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            IsRunning = false;
        }
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var target = _target;
            if (target is null || !TryGetBounds(target, out var bounds))
            {
                Stop();
                Failed?.Invoke(this, "Capture target not available");
                return;
            }

            if (bounds.Width < 16 || bounds.Height < 16)
            {
                return;
            }

            using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Drawing.Size(bounds.Width, bounds.Height),
                    Drawing.CopyPixelOperation.SourceCopy);
            }

            var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bounds.Width, bounds.Height),
                Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var length = data.Stride * data.Height;
                if (_buffer.Length != length)
                {
                    _buffer = new byte[length];
                }

                Marshal.Copy(data.Scan0, _buffer, 0, length);
                FrameCaptured?.Invoke(this, new CapturedFrame
                {
                    Pixels = _buffer, Width = data.Width, Height = data.Height, Stride = data.Stride,
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GDI capture failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static bool TryGetBounds(CaptureTarget target, out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (target.WindowHandle != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindow(target.WindowHandle) || NativeMethods.IsIconic(target.WindowHandle))
            {
                return false;
            }

            if (!NativeMethods.GetClientRect(target.WindowHandle, out var client))
            {
                return false;
            }

            var origin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(target.WindowHandle, ref origin);
            bounds = new NativeMethods.RECT
            {
                Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height,
            };
            return true;
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return false;
        }

        bounds = new NativeMethods.RECT
        {
            Left = screen.Bounds.Left, Top = screen.Bounds.Top, Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom,
        };
        return true;
    }

    public void Dispose() => Stop();
}
