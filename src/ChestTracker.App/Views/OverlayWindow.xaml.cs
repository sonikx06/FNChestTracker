using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ChestTracker.App.Interop;
using ChestTracker.App.ViewModels;

namespace ChestTracker.App.Views;

/// <summary>
/// Transparent, topmost, click-through counter window. It is a normal desktop window composed by
/// DWM above a borderless-windowed game: nothing is injected into the game. While not in edit mode
/// it has WS_EX_TRANSPARENT (mouse passes through) and WS_EX_NOACTIVATE (never takes focus).
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;
    private bool _positioned;

    public OverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        vm.PositionResetRequested += (_, _) =>
        {
            PlaceDefault();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyWindowStyles();
        };
        MouseLeftButtonDown += OnMouseDown;

        // Some games re-assert their own z-order; re-assert ours cheaply every few seconds.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
    }

    /// <summary>Applies visibility from the view model (call once after creation).</summary>
    public void Sync()
    {
        if (_vm.IsVisible)
        {
            if (!IsVisible)
            {
                Show();
            }

            if (!_positioned)
            {
                RestorePosition();
                _positioned = true;
            }

            ApplyWindowStyles();
            _topmostTimer.Start();
        }
        else
        {
            _topmostTimer.Stop();
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _topmostTimer.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        base.OnClosing(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.IsVisible):
                Sync();
                break;
            case nameof(OverlayViewModel.IsEditMode):
                if (_vm.IsEditMode && !_vm.IsVisible)
                {
                    _vm.IsVisible = true;
                }

                ApplyWindowStyles();
                break;
            case nameof(OverlayViewModel.ExcludeFromCapture):
                ApplyWindowStyles();
                break;
        }
    }

    private void ApplyWindowStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        if (_vm.IsEditMode)
        {
            ex &= ~(long)(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
        }
        else
        {
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
        }

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        NativeMethods.SetWindowDisplayAffinity(_hwnd,
            _vm.ExcludeFromCapture ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsEditMode || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _vm.SavePosition(Left, Top);
    }

    private void RestorePosition()
    {
        if (_vm.SavedLeft is { } left && _vm.SavedTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            PlaceDefault();
        }
    }

    private void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + 24;
        Top = area.Top + (area.Height * 0.28);
    }

    private static bool IsOnScreen(double left, double top)
    {
        // Keep at least a corner of the overlay on the virtual desktop (monitors can be unplugged).
        return left >= SystemParameters.VirtualScreenLeft - 40
               && top >= SystemParameters.VirtualScreenTop - 40
               && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
               && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
    }
}
