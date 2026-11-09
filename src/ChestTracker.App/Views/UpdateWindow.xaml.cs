using System.Windows;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Updates;

namespace ChestTracker.App.Views;

/// <summary>Shows an available update; "Update now" downloads, verifies and hands over to the updater.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly ReleaseInfo _release;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateService updates, ReleaseInfo release)
    {
        InitializeComponent();
        _updates = updates;
        _release = release;
        VersionText.Text = Loc.F("Update.Version", release.Version, updates.CurrentVersion)
                           + (release.Prerelease ? " " + Loc.T("Update.Prerelease") : "");
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? release.PageUrl : release.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    /// <summary>True when the updater was started and the app must exit now.</summary>
    public bool ReadyToInstall { get; private set; }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LaterButton.Content = Loc.T("Common.Cancel");
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = Loc.T("Update.Downloading");
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            Progress.Value = p;
            StatusText.Text = Loc.F("Update.DownloadingPercent", (int)(p * 100));
        });
        try
        {
            await _updates.DownloadAndStartUpdaterAsync(_release, progress, _cts.Token);
            StatusText.Text = Loc.T("Update.Installing");
            ReadyToInstall = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Update.Cancelled");
            Reset();
        }
        catch (Exception ex)
        {
            Log.Warn("Update failed before installation; the current version keeps running", ex);
            StatusText.Text = Loc.F("Update.Failed", ex.Message.TrimEnd('.'));
            Reset();
        }
    }

    private void Reset()
    {
        UpdateButton.IsEnabled = true;
        LaterButton.Content = Loc.T("Update.Later");
        Progress.Visibility = Visibility.Collapsed;
        _cts = null;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        DialogResult = false;
    }
}
