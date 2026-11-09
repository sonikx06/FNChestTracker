using System.Windows;
using System.Windows.Threading;
using ChestTracker.App.Capture;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.App.ViewModels;
using ChestTracker.App.Views;
using ChestTracker.Core;
using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Sync;

namespace ChestTracker.App;

public partial class App : Application
{
    private const string MutexName = "FortniteChestTracker.SingleInstance";
    private const string ShowEventName = "FortniteChestTracker.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppServices? _services;
    private MainViewModel? _main;
    private MainWindow? _window;
    private OverlayWindow? _overlay;
    private TrayService? _tray;
    private DiscordPresenceService? _discord;
    private PresenceCoordinator? _presence;
    private UpdateService? _updates;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Updater process (a copy of the previous executable): install the downloaded version, then exit.
        if (e.Args.Contains("--apply-update"))
        {
            Shutdown(UpdaterMode.Run(e.Args));
            return;
        }

        // Developer helper: render the Discord art assets and exit.
        var exportIndex = Array.IndexOf(e.Args, "--export-discord-assets");
        if (exportIndex >= 0)
        {
            var dir = exportIndex + 1 < e.Args.Length ? e.Args[exportIndex + 1] : "discord-assets";
            foreach (var file in IconFactory.ExportDiscordAssets(System.IO.Path.GetFullPath(dir)))
            {
                Console.WriteLine(file);
            }

            Shutdown();
            return;
        }

        _mutex =new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Another instance owns the hotkeys and the database: bring it to the front instead.
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        var paths = AppPaths.CreateDefault();
        Log.Initialize(paths.LogDirectory);
        Log.Info($"Starting Fortnite Chest Tracker {typeof(App).Assembly.GetName().Version}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var store = new SettingsStore(paths.SettingsFile);
        var settings = store.Load();
        Loc.SetLanguage(settings.Language);

        var backups = new BackupService(paths.BackupDirectory);
        SqliteDatabase db;
        DatabaseOpenReport report;
        try
        {
            (db, report) = DatabaseBootstrapper.Open(paths.DatabaseFile, backups);
        }
        catch (Exception ex)
        {
            Log.Error("Database could not be opened", ex);
            MessageBox.Show(Loc.F("Toast.Error", ex.Message), "Fortnite Chest Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            backups.CreateDailyBackupIfNeeded(db);
        }
        catch (Exception ex)
        {
            Log.Warn("Daily backup failed", ex);
        }

        var repo = new ChestRepository(db);
        var engine = new TrackingEngine(repo, SystemClock.Instance, settings.ToTrackingOptions(), settings.TrackingMode);
        var recovery = engine.Initialize();

        var hotkeys = new HotkeyService();
        hotkeys.Initialize();
        var detection = new DetectionService(engine, settings.Detection, settings.TrackingMode);
        var sounds = new SoundService { Enabled = settings.General.SoundsEnabled, Volume = settings.General.SoundVolume };
        var accounts = new DpapiAccountStore(settings, store);
        var sync = new SyncClient(engine, new SyncOutbox(db), accounts, () => new SyncPreferences
        {
            AutoShareMatches = settings.Multiplayer.AutoShareMatches,
            AutoJoinSharedMatches = settings.Multiplayer.AutoJoinSharedMatches,
            AutoConnect = settings.Multiplayer.AutoConnect,
        });

        _services = new AppServices
        {
            Paths = paths,
            Database = db,
            Repository = repo,
            Backups = backups,
            Engine = engine,
            SettingsStore = store,
            Settings = settings,
            Hotkeys = hotkeys,
            Detection = detection,
            Sounds = sounds,
            Dialogs = new DialogService(),
            Sync = sync,
            Accounts = accounts,
        };

        _main = new MainViewModel(_services);
        hotkeys.Apply(settings.Hotkeys.Bindings, settings.Hotkeys.Enabled);
        sync.Start(); // works offline; connects in the background when an account exists

        // Discord Rich Presence: local IPC with the Discord client only; no Discord login or token is ever used.
        _discord = new DiscordPresenceService();
        _presence = new PresenceCoordinator(_main, _discord);
        _main.Settings.Discord.Attach(_presence);

        _window = new MainWindow { DataContext = _main };
        MainWindow = _window;
        _overlay = new OverlayWindow(_main.Overlay);

        _tray = new TrayService();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ToggleOverlayRequested += (_, _) => _main.Settings.OverlayVisible = !_main.Settings.OverlayVisible;
        _tray.AddChestRequested += (_, _) => _main.AddChest(fromHotkey: false);
        _tray.StartMatchRequested += (_, _) => _main.StartMatch();
        _tray.EndMatchRequested += (_, _) => _main.EndMatch();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _main.SnapshotChanged += (_, s) => _tray.UpdateTooltip(s.MatchCount, s.LifetimeCount);
        _tray.UpdateTooltip(_main.Snapshot.MatchCount, _main.Snapshot.LifetimeCount);

        StartShowSignalListener();

        var startHidden = settings.General.StartMinimized;
        if (!startHidden)
        {
            _window.Show();
        }

        _overlay.Sync();
        ReportStartup(report, recovery);
        SessionEnding += (_, _) => ExitApplication();

        // Started by the updater: confirm a successful start (otherwise the updater restores the previous version).
        var markerIndex = Array.IndexOf(e.Args, UpdaterMode.StartedMarkerArg);
        if (markerIndex >= 0 && markerIndex + 1 < e.Args.Length)
        {
            try
            {
                System.IO.File.WriteAllText(e.Args[markerIndex + 1], UpdateService.Current().ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Could not confirm the update", ex);
            }
        }

        if (UpdateService.TakeResult() is { } last)
        {
            _main.ShowToast(last.Success ? Loc.F("Toast.Updated", last.To) : last.Message, last.Success ? ToastKind.Success : ToastKind.Error);
        }

        StartUpdateCheck();
    }

    /// <summary>Checks GitHub in the background; never delays or blocks startup, offline is fine.</summary>
    private void StartUpdateCheck()
    {
        _updates = new UpdateService();
        var updates = _updates;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var result = await updates.CheckAsync();
            if (result is { Status: Core.Updates.UpdateCheckStatus.Available, Release: { } release })
            {
                await Dispatcher.BeginInvoke(() => OfferUpdate(release));
            }
        });
    }

    private void OfferUpdate(Core.Updates.ReleaseInfo release)
    {
        if (_shuttingDown || _updates is null)
        {
            return;
        }

        var dialog = new UpdateWindow(_updates, release);
        if (_window is { IsVisible: true })
        {
            dialog.Owner = _window;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (dialog.ShowDialog() == true && dialog.ReadyToInstall)
        {
            ExitApplication(); // the updater waits for this process to exit before replacing any file
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void ExitApplication()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Log.Info("Shutting down");
        try
        {
            _main?.Settings.SaveNow();
            _services?.Detection.Dispose();
            _presence?.Dispose();
            if (_discord is not null)
            {
                // Clears the activity so Discord does not keep showing a stale status.
                Task.Run(() => _discord.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
            }

            if (_services is not null)
            {
                // Pending events stay in the durable outbox and are sent on next start.
                Task.Run(() => _services.Sync.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
            }

            _services?.Hotkeys.Dispose();
            _services?.Engine.Shutdown();
            _services?.Sounds.Dispose();
            _tray?.Dispose();
            _overlay?.Close();
            _main?.Dispose();
            _services?.Database.Dispose();
            _updates?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown", ex);
        }

        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shuttingDown && _services is not null)
        {
            ExitApplication();
        }

        _showEvent?.Dispose();
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void StartShowSignalListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (!_shuttingDown)
            {
                try
                {
                    if (_showEvent.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        Dispatcher.BeginInvoke(ShowMainWindow);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShowSignal",
        };
        thread.Start();
    }

    private void ReportStartup(DatabaseOpenReport report, RecoveryReport recovery)
    {
        if (_main is null)
        {
            return;
        }

        if (report.RestoredFromBackup)
        {
            _main.ShowToast(Loc.T("Toast.DbRestored"), ToastKind.Warning);
        }
        else if (report.CorruptFileMovedTo is not null)
        {
            _main.ShowToast(Loc.T("Toast.DbRecreated"), ToastKind.Error);
        }
        else if (recovery.ResumedMatchId is not null)
        {
            _main.ShowToast(Loc.T("Toast.Resumed"), ToastKind.Warning);
        }
        else if (recovery.ClosedMatches > 0)
        {
            _main.ShowToast(Loc.F("Toast.Recovered", recovery.ClosedMatches), ToastKind.Warning);
        }

        var problems = _main.Settings.HotkeyProblems;
        if (problems is not null)
        {
            _main.ShowToast(problems, ToastKind.Warning);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        _main?.ShowToast(Loc.F("Toast.Error", e.Exception.Message), ToastKind.Error);
    }
}
