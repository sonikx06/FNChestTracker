using System.IO;
using ChestTracker.Core.Detection;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Vision;
using OpenCvSharp;

namespace ChestTracker.App.Capture;

public enum DetectionStatus
{
    Disabled,
    Unsupported,
    WaitingForGame,
    Running,
    Candidate,
    Detected,
    Error,
}

/// <param name="Detail">Supporting evidence (YOLO: frames per state, confidences, state path).</param>
public sealed record DetectionLogEntry(DateTimeOffset Timestamp, DetectionOutcome Outcome, double Confidence, string Result, string? Detail = null);

public sealed class FrameAnalyzedEventArgs(FrameAnalysis analysis, double confidence, DetectorState state, PipelineFrame? yolo = null) : EventArgs
{
    public FrameAnalysis Analysis { get; } = analysis;
    public double Confidence { get; } = confidence;
    public DetectorState State { get; } = state;

    /// <summary>Set when the YOLO engine analysed the frame.</summary>
    public PipelineFrame? Yolo { get; } = yolo;
}

/// <summary>
/// EXPERIMENTAL. Runs capture → analysis → detector only while the tracking mode is
/// automatic or hybrid. If capture is unsupported or fails, it reports the status and
/// manual tracking keeps working untouched.
/// </summary>
public sealed class DetectionService : IDisposable
{
    private readonly TrackingEngine _engine;
    private readonly string _templatePath;
    private readonly FrameAnalyzer _analyzer = new();
    private readonly object _gate = new();
    private readonly Timer _watchdog;
    private ICaptureSource? _source;
    private ChestDetector _detector;
    private DetectionSettings _settings;
    private TrackingMode _mode;
    private DetectionStatus _status = DetectionStatus.Disabled;
    private DateTimeOffset _detectedUntil;
    private volatile bool _captureTemplateRequested;
    private bool _disposed;
    private YoloChestPipeline? _yolo;
    private string? _yoloPath;
    private int _busy;

    public DetectionService(TrackingEngine engine, DetectionSettings settings, TrackingMode mode, string templatePath)
    {
        _engine = engine;
        _templatePath = templatePath;
        _settings = Clone(settings);
        _mode = mode;
        _detector = new ChestDetector(Tuning(_settings));
        try
        {
            _analyzer.LoadTemplate(templatePath);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load detection template", ex);
        }

        IsCaptureSupported = WgcCaptureSource.IsSupported();
        _watchdog = new Timer(_ => Watchdog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    public event EventHandler? StatusChanged;

    /// <summary>Raised on a worker thread for every analysed frame (calibration UI).</summary>
    public event EventHandler<FrameAnalyzedEventArgs>? FrameAnalyzed;

    /// <summary>Raised on a worker thread for every finished candidate.</summary>
    public event EventHandler<DetectionLogEntry>? Decision;

    public event EventHandler? TemplateChanged;

    public bool IsCaptureSupported { get; }

    public bool HasTemplate => _analyzer.HasTemplate;

    /// <summary>Set while the calibration page is visible; previews cost extra CPU.</summary>
    public bool PreviewRequested { get; set; }

    public string? LastError { get; private set; }

    /// <summary>Why the YOLO model is not running (missing file, invalid model), null when it is loaded or unused.</summary>
    public string? ModelError { get; private set; }

    /// <summary>Loaded model description, e.g. "chest-yolov8n.onnx · 640 px · 5 classes (model metadata)".</summary>
    public string? ModelDescription { get; private set; }

    public bool UsesYolo => _settings.Engine == DetectionEngine.Yolo;

    /// <summary>Model used when no path is configured.</summary>
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "chest-yolov8.onnx");

    public string? TargetDescription { get; private set; }

    public DetectionStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsActiveMode => _mode is TrackingMode.Automatic or TrackingMode.Hybrid;

    public void Configure(DetectionSettings settings, TrackingMode mode)
    {
        bool restart;
        lock (_gate)
        {
            var old = _settings;
            _settings = Clone(settings);
            restart = old.Backend != _settings.Backend || old.Target != _settings.Target
                      || old.Engine != _settings.Engine || !string.Equals(old.YoloModelPath, _settings.YoloModelPath, StringComparison.OrdinalIgnoreCase)
                      || Math.Abs(old.SamplingFps - _settings.SamplingFps) > 0.01
                      || !string.Equals(old.ProcessName, _settings.ProcessName, StringComparison.OrdinalIgnoreCase)
                      || _mode != mode;
            _mode = mode;
            _detector.Tuning = Tuning(_settings);
            if (_yolo?.Detector is OnnxYoloDetector onnx)
            {
                onnx.ConfidenceThreshold = (float)_settings.YoloConfidence;
                _yolo.Tracker.Options = TrackingOptionsFor(_settings);
            }
        }

        if (restart)
        {
            Restart();
        }
    }

    public void Restart()
    {
        StopCapture();
        _detector.Reset();
        lock (_gate)
        {
            _yolo?.Reset();
        }

        Watchdog();
    }

    /// <summary>Loads (or unloads) the ONNX model for the configured engine. Errors leave manual tracking untouched.</summary>
    private void EnsureModelLocked(DetectionSettings settings)
    {
        if (settings.Engine != DetectionEngine.Yolo)
        {
            _yolo?.Dispose();
            _yolo = null;
            _yoloPath = null;
            ModelError = null;
            ModelDescription = null;
            return;
        }

        var path = string.IsNullOrWhiteSpace(settings.YoloModelPath) ? DefaultModelPath : settings.YoloModelPath.Trim();
        if (_yolo is not null && string.Equals(path, _yoloPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _yolo?.Dispose();
        _yolo = null;
        _yoloPath = path;
        try
        {
            var detector = OnnxYoloDetector.Load(path, threads: Math.Max(1, Environment.ProcessorCount / 4));
            detector.ConfidenceThreshold = (float)settings.YoloConfidence;
            _yolo = new YoloChestPipeline(detector, TrackingOptionsFor(settings));
            var info = detector.Info;
            var chestClasses = info.Labels.Roles.Count(r => r is ObjectRole.ChestClosed or ObjectRole.ChestOpening or ObjectRole.ChestOpened);
            ModelDescription = $"{Path.GetFileName(path)} · {info.InputSize} px · {info.Labels.Count} classes ({info.LabelSource})";
            ModelError = chestClasses < 3
                ? "The model has no chest_closed / chest_opening / chest_opened classes: nothing will be counted."
                : null;
            Log.Info($"YOLO model loaded: {ModelDescription}; classes: {string.Join(", ", info.Labels.Names)}");
        }
        catch (Exception ex)
        {
            ModelError = ex is FileNotFoundException ? $"Model file not found: {path}" : ex.Message;
            ModelDescription = null;
            Log.Warn("YOLO model could not be loaded; manual tracking continues", ex);
        }
    }

    private static ChestTrackingOptions TrackingOptionsFor(DetectionSettings s)
    {
        var tuning = Tuning(s);
        return new ChestTrackingOptions
        {
            MinDetectionConfidence = (float)s.YoloConfidence,
            ConfirmThreshold = tuning.ConfirmThreshold,
            ReviewThreshold = tuning.ReviewThreshold,
        };
    }

    public void RequestTemplateCapture() => _captureTemplateRequested = true;

    public void ClearTemplate()
    {
        _analyzer.ClearTemplate(_templatePath);
        TemplateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Watchdog()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            DetectionSettings settings;
            lock (_gate)
            {
                settings = _settings;
                if (!IsActiveMode)
                {
                    StopCaptureLocked();
                    SetStatusLocked(DetectionStatus.Disabled);
                    return;
                }

                if (_source?.IsRunning == true)
                {
                    if (_status == DetectionStatus.Detected && DateTimeOffset.UtcNow > _detectedUntil)
                    {
                        SetStatusLocked(DetectionStatus.Running);
                    }

                    return;
                }
            }

            lock (_gate)
            {
                EnsureModelLocked(settings);
                if (settings.Engine == DetectionEngine.Yolo && _yolo is null)
                {
                    SetStatusLocked(DetectionStatus.Error);
                    LastError = ModelError;
                    return;
                }
            }

            if (settings.Backend == CaptureBackend.WindowsGraphicsCapture && !IsCaptureSupported)
            {
                lock (_gate) SetStatusLocked(DetectionStatus.Unsupported);
                return;
            }

            var target = settings.Target == CaptureTargetKind.AutoFortnite
                ? WindowFinder.FindGameWindow(settings.ProcessName)
                : WindowFinder.PrimaryMonitor();
            if (target is null)
            {
                lock (_gate) SetStatusLocked(DetectionStatus.WaitingForGame);
                return;
            }

            ICaptureSource source = settings.Backend == CaptureBackend.GdiScreen
                ? new GdiCaptureSource()
                : new WgcCaptureSource();
            source.FrameCaptured += OnFrame;
            source.Failed += OnSourceFailed;
            try
            {
                source.Start(target, settings.SamplingFps);
            }
            catch (Exception ex)
            {
                source.FrameCaptured -= OnFrame;
                source.Failed -= OnSourceFailed;
                source.Dispose();
                LastError = ex.Message;
                Log.Warn("Capture could not start; manual tracking continues", ex);
                lock (_gate) SetStatusLocked(DetectionStatus.Error);
                return;
            }

            lock (_gate)
            {
                if (!IsActiveMode || _disposed)
                {
                    source.Dispose();
                    return;
                }

                _source = source;
                TargetDescription = target.Description;
                LastError = null;
                _analyzer.ResetMotion();
                _detector.Reset();
                SetStatusLocked(DetectionStatus.Running);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Detection watchdog failed", ex);
        }
    }

    private void OnSourceFailed(object? sender, string reason)
    {
        Log.Info("Capture stopped: " + reason);
        lock (_gate)
        {
            if (ReferenceEquals(sender, _source))
            {
                StopCaptureLocked();
                SetStatusLocked(IsActiveMode ? DetectionStatus.WaitingForGame : DetectionStatus.Disabled);
            }
        }
    }

    private void OnFrame(object? sender, CapturedFrame frame)
    {
        DetectionSettings settings;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _source))
            {
                return;
            }

            settings = _settings;
        }

        try
        {
            if (_captureTemplateRequested)
            {
                _captureTemplateRequested = false;
                _analyzer.CaptureTemplate(frame, settings, _templatePath);
                TemplateChanged?.Invoke(this, EventArgs.Empty);
            }

            if (settings.Engine == DetectionEngine.Yolo)
            {
                ProcessYolo(frame, settings);
                return;
            }

            var analysis = _analyzer.Analyze(frame, settings, PreviewRequested);
            var decision = _detector.Process(analysis.Features);
            var state = _detector.State;

            lock (_gate)
            {
                if (_status is DetectionStatus.Running or DetectionStatus.Candidate)
                {
                    SetStatusLocked(state == DetectorState.Candidate ? DetectionStatus.Candidate : DetectionStatus.Running);
                }
            }

            FrameAnalyzed?.Invoke(this, new FrameAnalyzedEventArgs(analysis, _detector.LastConfidence, state));

            if (decision is not null)
            {
                HandleDecision(decision, settings);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Frame analysis failed", ex);
        }
    }

    private void ProcessYolo(CapturedFrame frame, DetectionSettings settings)
    {
        // Inference can be slower than the capture rate: drop frames instead of queueing them.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            YoloChestPipeline? pipeline;
            lock (_gate)
            {
                pipeline = _yolo;
            }

            if (pipeline is null)
            {
                return;
            }

            var result = pipeline.Process(frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.Timestamp);
            var tracks = result.Update.Tracks;
            var best = tracks.Where(t => t.State is ChestState.Opening).Select(t => (double)t.LastConfidence).DefaultIfEmpty(0).Max();
            var state = tracks.Any(t => t.State == ChestState.Opening) ? DetectorState.Candidate : DetectorState.Idle;
            lock (_gate)
            {
                if (_status is DetectionStatus.Running or DetectionStatus.Candidate)
                {
                    SetStatusLocked(state == DetectorState.Candidate ? DetectionStatus.Candidate : DetectionStatus.Running);
                }
            }

            var preview = PreviewRequested ? DebugPreview(frame, result, settings.DebugView) : null;
            var analysis = new FrameAnalysis(new FrameFeatures(frame.Timestamp, 0, 0, 0, null), preview, result.ElapsedMs, frame.Width, frame.Height);
            FrameAnalyzed?.Invoke(this, new FrameAnalyzedEventArgs(analysis, best, state, result));

            foreach (var ev in result.Update.Events)
            {
                Log.Info($"YOLO opening {ev.Outcome} conf={ev.Confidence:0.00} box={ev.Box} {ev.Evidence}");
                HandleDecision(ev.ToDecision(), settings, ev.Evidence.ToString());
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Debug view: the frame (≤ 640 px wide) with every detection box, track id and chest state.</summary>
    private static PreviewImage DebugPreview(CapturedFrame frame, PipelineFrame result, bool drawBoxes)
    {
        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels, frame.Stride);
        var scale = Math.Min(1.0, 640.0 / frame.Width);
        var size = new OpenCvSharp.Size(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale)));
        using var small = new Mat();
        Cv2.Resize(source, small, size, 0, 0, InterpolationFlags.Area);

        if (drawBoxes)
        {
            OpenCvSharp.Rect R(Vision.Box b) => new((int)(b.X1 * scale), (int)(b.Y1 * scale), Math.Max(1, (int)(b.Width * scale)), Math.Max(1, (int)(b.Height * scale)));

            foreach (var d in result.Detections.Where(d => !d.IsChest))
            {
                Cv2.Rectangle(small, R(d.Box), new Scalar(160, 160, 160, 255), 1);
                Cv2.PutText(small, $"{d.Label} {d.Confidence:0.00}", new OpenCvSharp.Point(R(d.Box).X, Math.Max(10, R(d.Box).Y - 3)),
                    HersheyFonts.HersheySimplex, 0.35, new Scalar(200, 200, 200, 255));
            }

            foreach (var t in result.Update.Tracks.Where(t => t.MissedFrames == 0))
            {
                // BGR(A): closed = blue, opening = yellow, opened = gray, counted = green.
                var color = t.State switch
                {
                    ChestState.Opening => new Scalar(0, 220, 255, 255),
                    ChestState.Counted => new Scalar(80, 220, 80, 255),
                    ChestState.Opened => new Scalar(150, 150, 150, 255),
                    ChestState.Closed => new Scalar(255, 160, 60, 255),
                    _ => new Scalar(255, 255, 255, 255),
                };
                var r = R(t.Box);
                Cv2.Rectangle(small, r, color, 2);
                Cv2.PutText(small, $"#{t.Id} {t.State} {t.LastConfidence:0.00}", new OpenCvSharp.Point(r.X, Math.Max(10, r.Y - 4)),
                    HersheyFonts.HersheySimplex, 0.4, color);
            }
        }

        var bytes = new byte[small.Width * small.Height * 4];
        using var copy = small.IsContinuous() ? null : small.Clone(); // Clone() is always continuous
        System.Runtime.InteropServices.Marshal.Copy((copy ?? small).Data, bytes, 0, bytes.Length);

        return new PreviewImage(bytes, small.Width, small.Height);
    }

    private void HandleDecision(DetectionDecision decision, DetectionSettings settings, string? detail = null)
    {
        string result;
        if (decision.Outcome == DetectionOutcome.Discarded)
        {
            result = "discarded";
        }
        else if (settings.TestMode)
        {
            result = "test";
        }
        else if (decision.Outcome == DetectionOutcome.Confirmed)
        {
            var r = _engine.AddChestAutomatic(decision.Confidence, decision.Timestamp);
            result = r.Applied ? "counted" : "suppressed";
        }
        else if (settings.QueueUncertain)
        {
            _engine.AddPendingReview(decision.Confidence, decision.Timestamp);
            result = "review";
        }
        else
        {
            result = "discarded";
        }

        if (decision.Outcome == DetectionOutcome.Confirmed)
        {
            lock (_gate)
            {
                _detectedUntil = DateTimeOffset.UtcNow.AddSeconds(2);
                SetStatusLocked(DetectionStatus.Detected);
            }
        }

        Log.Info($"Detection {decision.Outcome} conf={decision.Confidence:0.00} frames={decision.Frames} -> {result}");
        Decision?.Invoke(this, new DetectionLogEntry(decision.Timestamp, decision.Outcome, decision.Confidence, result, detail));
    }

    private void StopCapture()
    {
        lock (_gate)
        {
            StopCaptureLocked();
        }
    }

    private void StopCaptureLocked()
    {
        if (_source is null)
        {
            return;
        }

        _source.FrameCaptured -= OnFrame;
        _source.Failed -= OnSourceFailed;
        try
        {
            _source.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture stop failed", ex);
        }

        _source = null;
        TargetDescription = null;
    }

    private void SetStatusLocked(DetectionStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        ThreadPool.QueueUserWorkItem(_ => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private static DetectionTuning Tuning(DetectionSettings s) =>
        DetectionTuning.FromSensitivity(s.Sensitivity, s.CooldownSeconds, s.SamplingFps);

    private static DetectionSettings Clone(DetectionSettings s) => new()
    {
        Sensitivity = s.Sensitivity, CooldownSeconds = s.CooldownSeconds, SamplingFps = s.SamplingFps,
        Backend = s.Backend, Target = s.Target, ProcessName = s.ProcessName, TestMode = s.TestMode,
        QueueUncertain = s.QueueUncertain, RoiWidth = s.RoiWidth, RoiHeight = s.RoiHeight, RoiCenterY = s.RoiCenterY,
        GoldHueMin = s.GoldHueMin, GoldHueMax = s.GoldHueMax, GoldSaturationMin = s.GoldSaturationMin,
        GoldValueMin = s.GoldValueMin, UseTemplate = s.UseTemplate, PromptX = s.PromptX, PromptY = s.PromptY,
        PromptWidth = s.PromptWidth, PromptHeight = s.PromptHeight,
        Engine = s.Engine, YoloModelPath = s.YoloModelPath, YoloConfidence = s.YoloConfidence, DebugView = s.DebugView,
    };

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Dispose();
        StopCapture();
        _analyzer.Dispose();
        lock (_gate)
        {
            _yolo?.Dispose();
            _yolo = null;
        }
    }
}
