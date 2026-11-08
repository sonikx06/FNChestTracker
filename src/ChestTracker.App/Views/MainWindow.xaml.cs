using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ChestTracker.App.Interop;
using ChestTracker.App.Services;
using ChestTracker.App.ViewModels;

namespace ChestTracker.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var icon = IconFactory.CreateBitmap(64);
        Icon = icon;
        Logo.Source = icon;
        SourceInitialized += (_, _) => ApplyDarkChrome();
        StateChanged += OnStateChanged;
    }

    /// <summary>Set by the application when it really exits.</summary>
    public bool AllowClose { get; set; }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (ViewModel?.Services.Settings.General.CloseToTray == true)
            {
                Hide();
            }
            else
            {
                // Exit after this Closing handler returns: Close() cannot be called while closing.
                Dispatcher.BeginInvoke(((App)Application.Current).ExitApplication);
            }
        }

        base.OnClosing(e);
    }

    /// <summary>Accessibility/keyboard selection checks a nav item without clicking it: navigate then too.</summary>
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.RadioButton { GroupName: "Nav", CommandParameter: string key }
            && ViewModel is { } vm && vm.CurrentPageKey != key)
        {
            vm.NavigateCommand.Execute(key);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && ViewModel?.Services.Settings.General.MinimizeToTray == true)
        {
            // Keep the Minimized state; App.ShowMainWindow restores it after Show().
            Hide();
        }
    }

    private void ApplyDarkChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // COLORREF 0x00BBGGRR of the sidebar color #120D20.
        var caption = 0x00200D12;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        var round = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }
}
