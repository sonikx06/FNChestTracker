using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChestTracker.App.Capture;
using ChestTracker.App.Controls;
using ChestTracker.App.Localization;
using ChestTracker.Core.Detection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestTracker.App.ViewModels;

public sealed class DetectionLogItem(DetectionLogEntry entry)
{
    public string TimeText { get; } = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string ConfidenceText { get; } = entry.Confidence.ToString("P0", Loc.Culture);
    public DetectionOutcome Outcome { get; } = entry.Outcome;
    public string? Detail { get; } = entry.Detail;

    public string ResultText { get; } = entry.Result switch
    {
        "counted" => Loc.T("Det.Outcome.Confirmed"),
        "review" => Loc.T("Det.Outcome.Uncertain"),
        "test" => Loc.T("Det.Outcome.Test"),
        "suppressed" => Loc.T("Det.Outcome.Suppressed"),
        "stale" => Loc.T("Det.Outcome.Stale"),
        _ => Loc.T("Det.Outcome.Discarded"),
    };
}

/// <summary>Live view and settings of the built-in chest model.</summary>
public sealed partial class DetectionViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 80;
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<double> _confidence = new();
    private WriteableBitmap? _bitmap;
    private bool _active;
    private int _pendingFrame;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _confidenceText = "—";
    [ObservableProperty] private double _confidenceValue;
    [ObservableProperty] private string _analysisText = "—";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _baselineText = "";
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private List<ChartPoint> _confidenceHistory = [];
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _yoloText;
    [ObservableProperty] private string? _modelText;
    [ObservableProperty] private string? _modelError;

    public DetectionViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var detection = main.Services.Detection;
        detection.FrameAnalyzed += OnFrameAnalyzed;
        detection.Decision += OnDecision;
        Loc.LanguageChanged += (_, _) => OnStatusChanged();
    }

    public SettingsViewModel Settings => _main.Settings;

    public ObservableCollection<DetectionLogItem> Log { get; } = [];

    public bool IsSupported => _main.Services.Detection.IsCaptureSupported;

    public bool IsActiveMode => _main.Services.Detection.IsActiveMode;

    public void SetActive(bool active)
    {
        _active = active;
        _main.Services.Detection.PreviewRequested = active;
        if (!active)
        {
            Preview = null;
            _bitmap = null;
        }
    }

    public void OnStatusChanged()
    {
        var d = _main.Services.Detection;
        StatusText = Loc.T("Det.Status." + d.Status);
        IsRunning = d.Status is DetectionStatus.Running or DetectionStatus.Candidate or DetectionStatus.Detected;
        SourceText = d.TargetDescription ?? "";
        ErrorText = d.Status == DetectionStatus.Error ? d.LastError : null;
        ModelText = d.ModelDescription ?? Loc.T("Det.ModelNotLoaded");
        ModelError = d.ModelError;
        OnPropertyChanged(nameof(IsActiveMode));
        if (!IsRunning)
        {
            ConfidenceText = AnalysisText = "—";
            ConfidenceValue = 0;
        }
    }

    [RelayCommand]
    private void Restart() => _main.Services.Detection.Restart();

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private void OnFrameAnalyzed(object? sender, FrameAnalyzedEventArgs e)
    {
        // Only the calibration page needs per-frame data; skip UI work otherwise and coalesce bursts.
        if (!_active || Interlocked.Exchange(ref _pendingFrame, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _pendingFrame, 0);
            ApplyFrame(e);
        });
    }

    private void ApplyFrame(FrameAnalyzedEventArgs e)
    {
        ConfidenceValue = e.Confidence;
        ConfidenceText = e.Confidence.ToString("P0", Loc.Culture);
        AnalysisText = $"{e.Analysis.ElapsedMs:0.0} ms";
        SourceText = $"{_main.Services.Detection.TargetDescription} · {e.Analysis.SourceWidth}×{e.Analysis.SourceHeight}";

        _confidence.Enqueue(e.Confidence);
        while (_confidence.Count > HistoryLength)
        {
            _confidence.Dequeue();
        }

        ConfidenceHistory = _confidence.Select(c => new ChartPoint("", Math.Round(c * 100, 1))).ToList();

        if (e.Yolo is { } y)
        {
            var tracks = y.Update.Tracks.Where(t => t.MissedFrames == 0).ToList();
            var states = string.Join(", ", tracks.GroupBy(t => t.State).Select(g => $"{g.Key} {g.Count()}"));
            YoloText = Loc.F("Det.YoloStats", y.Detections.Count, tracks.Count, states.Length == 0 ? "—" : states,
                y.Update.Containers.Count, y.ElapsedMs.ToString("0", CultureInfo.InvariantCulture));
        }

        if (e.Analysis.Preview is { } p)
        {
            if (_bitmap is null || _bitmap.PixelWidth != p.Width || _bitmap.PixelHeight != p.Height)
            {
                _bitmap = new WriteableBitmap(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null);
                Preview = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, p.Width, p.Height), p.Bgra, p.Width * 4, 0);
        }
    }

    private void OnDecision(object? sender, DetectionLogEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Log.Insert(0, new DetectionLogItem(entry));
            while (Log.Count > 100)
            {
                Log.RemoveAt(Log.Count - 1);
            }
        });
    }

    public void Dispose()
    {
        _main.Services.Detection.FrameAnalyzed -= OnFrameAnalyzed;
        _main.Services.Detection.Decision -= OnDecision;
    }
}
