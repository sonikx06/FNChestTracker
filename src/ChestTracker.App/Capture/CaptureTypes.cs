using System.Diagnostics;
using ChestTracker.App.Interop;

namespace ChestTracker.App.Capture;

/// <summary>
/// One captured BGRA frame. The pixel buffer is reused by the source: it is only valid during
/// the <see cref="ICaptureSource.FrameCaptured"/> callback.
/// </summary>
public sealed class CapturedFrame
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record CaptureTarget(IntPtr WindowHandle, IntPtr MonitorHandle, string Description);

public interface ICaptureSource : IDisposable
{
    string Name { get; }

    bool IsRunning { get; }

    /// <summary>Raised on a worker thread, at most at the requested sampling rate.</summary>
    event EventHandler<CapturedFrame>? FrameCaptured;

    /// <summary>Raised when capture stops unexpectedly (window closed, device lost...).</summary>
    event EventHandler<string>? Failed;

    void Start(CaptureTarget target, double framesPerSecond);

    void Stop();
}

public static class WindowFinder
{
    /// <summary>Finds the main window of the configured game process. Read-only: only window handles are queried.</summary>
    public static CaptureTarget? FindGameWindow(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return null;
        }

        try
        {
            foreach (var p in processes)
            {
                IntPtr hwnd;
                try
                {
                    hwnd = p.MainWindowHandle;
                }
                catch
                {
                    continue;
                }

                if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd))
                {
                    return new CaptureTarget(hwnd, IntPtr.Zero, p.ProcessName);
                }
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        return null;
    }

    public static CaptureTarget PrimaryMonitor()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return new CaptureTarget(IntPtr.Zero, monitor, "Primary monitor");
    }
}
