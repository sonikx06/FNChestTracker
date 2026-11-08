using ChestTracker.App.Capture;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core;
using ChestTracker.Core.Data;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChestTracker.App.ViewModels;

/// <summary>Everything the view models need, created once in App.OnStartup.</summary>
public sealed class AppServices
{
    public required AppPaths Paths { get; init; }
    public required SqliteDatabase Database { get; init; }
    public required ChestRepository Repository { get; init; }
    public required BackupService Backups { get; init; }
    public required TrackingEngine Engine { get; init; }
    public required SettingsStore SettingsStore { get; init; }
    public required AppSettings Settings { get; init; }
    public required HotkeyService Hotkeys { get; init; }
    public required DetectionService Detection { get; init; }
    public required SoundService Sounds { get; init; }
    public required DialogService Dialogs { get; init; }
    public required ChestTracker.Sync.SyncClient Sync { get; init; }
    public required DpapiAccountStore Accounts { get; init; }
}

/// <summary>A selectable option whose label follows the UI language.</summary>
public sealed class LocalizedOption<T> : ObservableObject
{
    private readonly string? _key;
    private readonly string? _fixedLabel;

    public LocalizedOption(T value, string key)
    {
        Value = value;
        _key = key;
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    private LocalizedOption(T value, string fixedLabel, bool _)
    {
        Value = value;
        _fixedLabel = fixedLabel;
    }

    public static LocalizedOption<T> Fixed(T value, string label) => new(value, label, true);

    public T Value { get; }

    public string Label => _fixedLabel ?? Loc.T(_key!);

    public override string ToString() => Label;
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}
