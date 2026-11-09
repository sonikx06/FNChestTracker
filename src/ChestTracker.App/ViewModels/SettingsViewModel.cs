using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ChestTracker.App.Controls;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Data;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ChestTracker.App.ViewModels;

public enum SettingsSection
{
    General,
    Language,
    Tracking,
    Mode,
    Hotkeys,
    Overlay,
    Detection,
    Multiplayer,
}

public sealed record OverlayPreset(string Name, string Text, string Accent, string Background, double BackgroundOpacity, bool ShowBackground)
{
    public static readonly IReadOnlyList<OverlayPreset> All =
    [
        new("Neon", "#FFFFFF", "#C084FC", "#140F22", 0.72, true),
        new("Gold", "#FFF7E0", "#FBBF24", "#1F1608", 0.70, true),
        new("Minimal", "#FFFFFF", "#FFFFFF", "#000000", 0.0, false),
        new("Stealth", "#D4D4D8", "#A1A1AA", "#000000", 0.35, true),
    ];
}

public sealed partial class HotkeyBindingViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private HotkeyStatus _status;

    public HotkeyBindingViewModel(SettingsViewModel owner, HotkeyAction action)
    {
        _owner = owner;
        Action = action;
    }

    public HotkeyAction Action { get; }

    public string Label => Loc.T("Hotkey." + Action);

    public HotkeyAssignment Binding
    {
        get => _owner.Model.Hotkeys.Get(Action).Clone();
        set
        {
            var assignment = new HotkeyAssignment { Action = Action, VirtualKey = value?.VirtualKey ?? 0, Modifiers = value?.Modifiers ?? HotkeyModifiers.None };
            _owner.SetBinding(assignment);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Clear() => Binding = new HotkeyAssignment { Action = Action };

    public void Refresh(HotkeyStatus status)
    {
        Status = status;
        StatusText = Loc.T("Hotkey.Status." + status switch
        {
            HotkeyStatus.Ok => "Ok",
            HotkeyStatus.Duplicate => "Duplicate",
            HotkeyStatus.External => "External",
            HotkeyStatus.Disabled => "Disabled",
            _ => "Unbound",
        });
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Binding));
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _saveTimer;
    private bool _applyingPreset;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Model = main.Services.Settings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();

        Languages = [LocalizedOption<string>.Fixed("ru", "Русский"), LocalizedOption<string>.Fixed("en", "English")];
        Layouts = Enum.GetValues<OverlayLayout>().Select(l => new LocalizedOption<OverlayLayout>(l, "Layout." + l)).ToList();
        Presets = [.. OverlayPreset.All.Select(p => new LocalizedOption<string>(p.Name, "Preset." + p.Name)), new LocalizedOption<string>("Custom", "Preset.Custom")];
        Backends = Enum.GetValues<CaptureBackend>().Select(b => new LocalizedOption<CaptureBackend>(b, "Backend." + b)).ToList();
        Targets = Enum.GetValues<CaptureTargetKind>().Select(t => new LocalizedOption<CaptureTargetKind>(t, "Target." + t)).ToList();
        Hotkeys = new ObservableCollection<HotkeyBindingViewModel>(Enum.GetValues<HotkeyAction>().Select(a => new HotkeyBindingViewModel(this, a)));
        Discord = new DiscordViewModel(main, ScheduleSave);

        main.Services.Hotkeys.StatusChanged += (_, _) => RefreshHotkeyStatus();
        HotkeyBox.CaptureStateChanged += (_, capturing) =>
        {
            if (capturing) main.Services.Hotkeys.Suspend();
            else main.Services.Hotkeys.Resume();
        };
        Loc.LanguageChanged += (_, _) => RefreshHotkeyStatus();
        RefreshHotkeyStatus();
    }

    public AppSettings Model { get; }

    public DiscordViewModel Discord { get; }

    public IReadOnlyList<LocalizedOption<string>> Languages { get; }
    public IReadOnlyList<LocalizedOption<OverlayLayout>> Layouts { get; }
    public IReadOnlyList<LocalizedOption<string>> Presets { get; }
    public IReadOnlyList<LocalizedOption<CaptureBackend>> Backends { get; }
    public IReadOnlyList<LocalizedOption<CaptureTargetKind>> Targets { get; }
    public ObservableCollection<HotkeyBindingViewModel> Hotkeys { get; }

    public string DataFolder => _main.Services.Paths.Root;

    // ---------------------------------------------------------------- general

    public LocalizedOption<string> SelectedLanguage
    {
        get => Languages.First(l => l.Value == Model.Language);
        set
        {
            if (value is null || value.Value == Model.Language) return;
            Model.Language = value.Value;
            Loc.SetLanguage(value.Value);
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool StartWithWindows
    {
        get => Model.General.StartWithWindows;
        set
        {
            if (Update(Model.General.StartWithWindows, value, v => Model.General.StartWithWindows = v, SettingsSection.General))
            {
                StartupService.Apply(value);
            }
        }
    }

    public bool StartMinimized
    {
        get => Model.General.StartMinimized;
        set => Update(Model.General.StartMinimized, value, v => Model.General.StartMinimized = v, SettingsSection.General);
    }

    public bool MinimizeToTray
    {
        get => Model.General.MinimizeToTray;
        set => Update(Model.General.MinimizeToTray, value, v => Model.General.MinimizeToTray = v, SettingsSection.General);
    }

    public bool CloseToTray
    {
        get => Model.General.CloseToTray;
        set => Update(Model.General.CloseToTray, value, v => Model.General.CloseToTray = v, SettingsSection.General);
    }

    public bool SoundsEnabled
    {
        get => Model.General.SoundsEnabled;
        set => Update(Model.General.SoundsEnabled, value, v => Model.General.SoundsEnabled = v, SettingsSection.General);
    }

    public double SoundVolume
    {
        get => Model.General.SoundVolume;
        set => Update(Model.General.SoundVolume, Math.Round(value, 2), v => Model.General.SoundVolume = v, SettingsSection.General);
    }

    // --------------------------------------------------------------- tracking

    public TrackingMode TrackingMode
    {
        get => Model.TrackingMode;
        set => Update(Model.TrackingMode, value, v => Model.TrackingMode = v, SettingsSection.Mode);
    }

    public double ManualDebounceMs
    {
        get => Model.Tracking.ManualDebounceMs;
        set => Update(Model.Tracking.ManualDebounceMs, (int)Math.Round(value / 10) * 10, v => Model.Tracking.ManualDebounceMs = v, SettingsSection.Tracking);
    }

    public double HybridMergeWindowSeconds
    {
        get => Model.Tracking.HybridMergeWindowSeconds;
        set => Update(Model.Tracking.HybridMergeWindowSeconds, Math.Round(value, 1), v => Model.Tracking.HybridMergeWindowSeconds = v, SettingsSection.Tracking);
    }

    public double ResumeWindowMinutes
    {
        get => Model.Tracking.ResumeWindowMinutes;
        set => Update(Model.Tracking.ResumeWindowMinutes, (int)Math.Round(value), v => Model.Tracking.ResumeWindowMinutes = v, SettingsSection.Tracking);
    }

    // ---------------------------------------------------------------- hotkeys

    public bool HotkeysEnabled
    {
        get => Model.Hotkeys.Enabled;
        set => Update(Model.Hotkeys.Enabled, value, v => Model.Hotkeys.Enabled = v, SettingsSection.Hotkeys);
    }

    internal void SetBinding(HotkeyAssignment assignment)
    {
        var list = Model.Hotkeys.Bindings;
        var index = list.FindIndex(b => b.Action == assignment.Action);
        if (index >= 0) list[index] = assignment;
        else list.Add(assignment);
        Changed(SettingsSection.Hotkeys);
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        Model.Hotkeys.Bindings = HotkeySettings.Defaults();
        Changed(SettingsSection.Hotkeys);
    }

    private void RefreshHotkeyStatus()
    {
        var status = _main.Services.Hotkeys.Status;
        foreach (var h in Hotkeys)
        {
            h.Refresh(status.TryGetValue(h.Action, out var s) ? s : HotkeyStatus.Unbound);
        }

        var failed = Hotkeys.Where(h => h.Status is HotkeyStatus.External or HotkeyStatus.Duplicate).Select(h => h.Label).ToList();
        HotkeyProblems = failed.Count == 0 ? null : Loc.F("Toast.HotkeyConflict", string.Join(", ", failed));
    }

    [ObservableProperty] private string? _hotkeyProblems;

    // ---------------------------------------------------------------- overlay

    public bool OverlayVisible
    {
        get => Model.Overlay.Visible;
        set => Update(Model.Overlay.Visible, value, v => Model.Overlay.Visible = v, SettingsSection.Overlay);
    }

    public OverlayLayout OverlayLayout
    {
        get => Model.Overlay.Layout;
        set => Update(Model.Overlay.Layout, value, v => Model.Overlay.Layout = v, SettingsSection.Overlay);
    }

    public LocalizedOption<OverlayLayout> SelectedLayout
    {
        get => Layouts.First(l => l.Value == Model.Overlay.Layout);
        set
        {
            if (value is not null)
            {
                OverlayLayout = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public LocalizedOption<string> SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Value == Model.Overlay.Preset) ?? Presets[^1];
        set
        {
            if (value is null || value.Value == Model.Overlay.Preset) return;
            Model.Overlay.Preset = value.Value;
            var preset = OverlayPreset.All.FirstOrDefault(p => p.Name == value.Value);
            if (preset is not null)
            {
                _applyingPreset = true;
                OverlayTextColor = preset.Text;
                OverlayAccentColor = preset.Accent;
                OverlayBackgroundColor = preset.Background;
                OverlayBackgroundOpacity = preset.BackgroundOpacity;
                OverlayShowBackground = preset.ShowBackground;
                _applyingPreset = false;
            }

            OnPropertyChanged();
            Changed(SettingsSection.Overlay);
        }
    }

    public double OverlayOpacity
    {
        get => Model.Overlay.Opacity;
        set => Update(Model.Overlay.Opacity, Math.Round(value, 2), v => Model.Overlay.Opacity = v, SettingsSection.Overlay);
    }

    public double OverlayScale
    {
        get => Model.Overlay.Scale;
        set => Update(Model.Overlay.Scale, Math.Round(value, 2), v => Model.Overlay.Scale = v, SettingsSection.Overlay);
    }

    public string OverlayTextColor
    {
        get => Model.Overlay.TextColor;
        set => UpdateColor(Model.Overlay.TextColor, value, v => Model.Overlay.TextColor = v);
    }

    public string OverlayAccentColor
    {
        get => Model.Overlay.AccentColor;
        set => UpdateColor(Model.Overlay.AccentColor, value, v => Model.Overlay.AccentColor = v);
    }

    public string OverlayBackgroundColor
    {
        get => Model.Overlay.BackgroundColor;
        set => UpdateColor(Model.Overlay.BackgroundColor, value, v => Model.Overlay.BackgroundColor = v);
    }

    public bool OverlayShowBackground
    {
        get => Model.Overlay.ShowBackground;
        set
        {
            if (Update(Model.Overlay.ShowBackground, value, v => Model.Overlay.ShowBackground = v, SettingsSection.Overlay)) MarkCustomPreset();
        }
    }

    public double OverlayBackgroundOpacity
    {
        get => Model.Overlay.BackgroundOpacity;
        set
        {
            if (Update(Model.Overlay.BackgroundOpacity, Math.Round(value, 2), v => Model.Overlay.BackgroundOpacity = v, SettingsSection.Overlay)) MarkCustomPreset();
        }
    }

    public bool OverlayShowTimer
    {
        get => Model.Overlay.ShowMatchTimer;
        set => Update(Model.Overlay.ShowMatchTimer, value, v => Model.Overlay.ShowMatchTimer = v, SettingsSection.Overlay);
    }

    public bool OverlayShowDetection
    {
        get => Model.Overlay.ShowDetectionStatus;
        set => Update(Model.Overlay.ShowDetectionStatus, value, v => Model.Overlay.ShowDetectionStatus = v, SettingsSection.Overlay);
    }

    public bool OverlayExcludeFromCapture
    {
        get => Model.Overlay.ExcludeFromCapture;
        set => Update(Model.Overlay.ExcludeFromCapture, value, v => Model.Overlay.ExcludeFromCapture = v, SettingsSection.Overlay);
    }

    public void SetOverlayPosition(double left, double top)
    {
        Model.Overlay.Left = Math.Round(left);
        Model.Overlay.Top = Math.Round(top);
        ScheduleSave();
    }

    [RelayCommand]
    private void ResetOverlayPosition()
    {
        Model.Overlay.Left = null;
        Model.Overlay.Top = null;
        Changed(SettingsSection.Overlay);
        _main.Overlay.ResetPosition();
    }

    [RelayCommand]
    private void SetAccent(string color) => OverlayAccentColor = color;

    private void UpdateColor(string current, string value, Action<string> apply)
    {
        var normalized = NormalizeColor(value);
        if (normalized is null)
        {
            // Invalid input: re-read the stored value so the textbox reverts.
            OnPropertyChanged(nameof(OverlayTextColor));
            OnPropertyChanged(nameof(OverlayAccentColor));
            OnPropertyChanged(nameof(OverlayBackgroundColor));
            return;
        }

        if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)) return;
        apply(normalized);
        OnPropertyChanged(nameof(OverlayTextColor));
        OnPropertyChanged(nameof(OverlayAccentColor));
        OnPropertyChanged(nameof(OverlayBackgroundColor));
        MarkCustomPreset();
        Changed(SettingsSection.Overlay);
    }

    private void MarkCustomPreset()
    {
        if (_applyingPreset || Model.Overlay.Preset == "Custom") return;
        Model.Overlay.Preset = "Custom";
        OnPropertyChanged(nameof(SelectedPreset));
    }

    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (!v.StartsWith('#')) v = "#" + v;
        return (v.Length is 7 or 9) && v.Skip(1).All(Uri.IsHexDigit) ? v.ToUpperInvariant() : null;
    }

    // ------------------------------------------------------------ multiplayer

    public bool AutoShareMatches
    {
        get => Model.Multiplayer.AutoShareMatches;
        set => Update(Model.Multiplayer.AutoShareMatches, value, v => Model.Multiplayer.AutoShareMatches = v, SettingsSection.Multiplayer);
    }

    public bool AutoJoinSharedMatches
    {
        get => Model.Multiplayer.AutoJoinSharedMatches;
        set => Update(Model.Multiplayer.AutoJoinSharedMatches, value, v => Model.Multiplayer.AutoJoinSharedMatches = v, SettingsSection.Multiplayer);
    }

    public bool NotifyTeammateChests
    {
        get => Model.Multiplayer.NotifyTeammateChests;
        set => Update(Model.Multiplayer.NotifyTeammateChests, value, v => Model.Multiplayer.NotifyTeammateChests = v, SettingsSection.Multiplayer);
    }

    public bool OverlayTeamMode
    {
        get => Model.Multiplayer.OverlayTeamMode;
        set => Update(Model.Multiplayer.OverlayTeamMode, value, v => Model.Multiplayer.OverlayTeamMode = v, SettingsSection.Multiplayer);
    }

    public bool OverlayShowActivity
    {
        get => Model.Multiplayer.OverlayShowActivity;
        set => Update(Model.Multiplayer.OverlayShowActivity, value, v => Model.Multiplayer.OverlayShowActivity = v, SettingsSection.Multiplayer);
    }

    // -------------------------------------------------------------- detection

    public int Sensitivity
    {
        get => Model.Detection.Sensitivity;
        set => Update(Model.Detection.Sensitivity, value, v => Model.Detection.Sensitivity = v, SettingsSection.Detection);
    }

    public LocalizedOption<CaptureBackend> SelectedBackend
    {
        get => Backends.First(b => b.Value == Model.Detection.Backend);
        set
        {
            if (value is not null && Update(Model.Detection.Backend, value.Value, v => Model.Detection.Backend = v, SettingsSection.Detection))
                OnPropertyChanged();
        }
    }

    public LocalizedOption<CaptureTargetKind> SelectedTarget
    {
        get => Targets.First(t => t.Value == Model.Detection.Target);
        set
        {
            if (value is not null && Update(Model.Detection.Target, value.Value, v => Model.Detection.Target = v, SettingsSection.Detection))
                OnPropertyChanged();
        }
    }

    public bool TestMode
    {
        get => Model.Detection.TestMode;
        set => Update(Model.Detection.TestMode, value, v => Model.Detection.TestMode = v, SettingsSection.Detection);
    }

    public double YoloConfidence
    {
        get => Model.Detection.YoloConfidence;
        set => Update(Model.Detection.YoloConfidence, Math.Round(Math.Clamp(value, 0.05, 0.95), 2), v => Model.Detection.YoloConfidence = v, SettingsSection.Detection);
    }

    public bool DebugView
    {
        get => Model.Detection.DebugView;
        set => Update(Model.Detection.DebugView, value, v => Model.Detection.DebugView = v, SettingsSection.Detection);
    }

    public bool QueueUncertain
    {
        get => Model.Detection.QueueUncertain;
        set => Update(Model.Detection.QueueUncertain, value, v => Model.Detection.QueueUncertain = v, SettingsSection.Detection);
    }

    [RelayCommand]
    private void ResetDetectionDefaults()
    {
        var d = new DetectionSettings();
        var m = Model.Detection;
        m.Sensitivity = d.Sensitivity;
        m.YoloConfidence = d.YoloConfidence;
        OnPropertyChanged(string.Empty);
        Changed(SettingsSection.Detection);
    }

    // ------------------------------------------------------------------- data

    [RelayCommand]
    private void ExportJson()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"chest-tracker-export-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog() != true) return;

        _main.Guard(() =>
        {
            DataExporter.ExportJson(_main.Services.Repository, dialog.FileName, DateTimeOffset.UtcNow);
            _main.ShowToast(Loc.F("Toast.Exported", System.IO.Path.GetFileName(dialog.FileName)), ToastKind.Success);
        });
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"chest-matches-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        _main.Guard(() =>
        {
            DataExporter.ExportMatchesCsv(_main.Services.Repository.GetMatchSummaries(), dialog.FileName, TimeZoneInfo.Local);
            _main.ShowToast(Loc.F("Toast.Exported", System.IO.Path.GetFileName(dialog.FileName)), ToastKind.Success);
        });
    }

    [RelayCommand]
    private void ImportJson()
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        if (!_main.Services.Dialogs.Confirm(Loc.T("Dialog.Import.Title"), Loc.T("Dialog.Import.Message"))) return;

        _main.Guard(() =>
        {
            _main.Services.Backups.CreateBackup(_main.Services.Database, "preimport");
            var result = DataExporter.ImportJson(_main.Services.Repository, dialog.FileName);
            _main.Services.Engine.NotifyImported();
            _main.ShowToast(Loc.F("Toast.Imported", result.Matches, result.Events, result.Skipped), ToastKind.Success);
        });
    }

    [RelayCommand]
    private void BackupNow() => _main.Guard(() =>
    {
        _main.Services.Backups.CreateBackup(_main.Services.Database, "manual");
        _main.ShowToast(Loc.T("Toast.BackupCreated"), ToastKind.Success);
    });

    [RelayCommand]
    private void OpenDataFolder() => _main.Guard(() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_main.Services.Paths.Root}\"") { UseShellExecute = true }));

    [RelayCommand]
    private void AdjustLifetime()
    {
        var value = _main.Services.Dialogs.PromptInt(Loc.T("Dialog.Lifetime.Title"), Loc.T("Dialog.Lifetime.Message"), 0, -100000, 100000);
        if (value is { } v && v != 0)
        {
            _main.Guard(() =>
            {
                _main.Services.Engine.AdjustLifetime(v);
                _main.ShowToast(Loc.T("Toast.LifetimeAdjusted"), ToastKind.Success);
            });
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        var word = Loc.T("Dialog.ResetAll.Word");
        var typed = _main.Services.Dialogs.PromptText(Loc.T("Dialog.ResetAll.Title"), Loc.T("Dialog.ResetAll.Message"), "", danger: true);
        if (typed is null || !string.Equals(typed.Trim(), word, StringComparison.CurrentCultureIgnoreCase)) return;

        _main.Guard(() =>
        {
            _main.Services.Backups.CreateBackup(_main.Services.Database, "prereset");
            _main.Services.Engine.ResetAllData();
            _main.ShowToast(Loc.T("Toast.Reset"), ToastKind.Success);
        });
    }

    [RelayCommand]
    private void TestSound()
    {
        var sounds = _main.Services.Sounds;
        var enabled = sounds.Enabled;
        sounds.Enabled = true;
        sounds.Play(Core.Tracking.SoundCue.ChestAdded);
        sounds.Enabled = enabled;
    }

    // --------------------------------------------------------------- plumbing

    private bool Update<T>(T current, T value, Action<T> apply, SettingsSection section, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        OnPropertyChanged(name);
        Changed(section);
        return true;
    }

    private void Changed(SettingsSection section)
    {
        var s = _main.Services;
        try
        {
            switch (section)
            {
                case SettingsSection.General:
                    s.Sounds.Enabled = Model.General.SoundsEnabled;
                    s.Sounds.Volume = Model.General.SoundVolume;
                    break;
                case SettingsSection.Tracking:
                    s.Engine.Options = Model.ToTrackingOptions();
                    break;
                case SettingsSection.Mode:
                    s.Engine.SetMode(Model.TrackingMode);
                    s.Detection.Configure(Model.Detection, Model.TrackingMode);
                    break;
                case SettingsSection.Hotkeys:
                    s.Hotkeys.Apply(Model.Hotkeys.Bindings, Model.Hotkeys.Enabled);
                    foreach (var h in Hotkeys) h.Refresh(s.Hotkeys.Status.TryGetValue(h.Action, out var st) ? st : HotkeyStatus.Unbound);
                    break;
                case SettingsSection.Overlay:
                    _main.Overlay.ApplySettings(Model.Overlay);
                    break;
                case SettingsSection.Multiplayer:
                    _main.Overlay.ApplyMultiplayer(Model.Multiplayer);
                    break;
                case SettingsSection.Detection:
                    Model.Normalize();
                    s.Engine.Options = Model.ToTrackingOptions();
                    s.Detection.Configure(Model.Detection, Model.TrackingMode);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Applying settings section {section} failed", ex);
        }

        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _main.Services.SettingsStore.Save(Model);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
