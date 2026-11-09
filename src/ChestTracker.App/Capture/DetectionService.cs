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
/// Runs capture → analysis → detector only while the tracking mode is automatic or hybrid. Exactly one engine is
/// active (YOLO by default, or the experimental heuristic), so two detectors can never count the same opening.
/// YOLO frames go to a dedicated inference thread (latest frame wins, older ones are dropped), so neither the
/// capture callback nor the UI thread ever waits for the model. If capture or the model fails, it reports the
/// status and manual tracking keeps working untouched.
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
    private readonly InferenceWorker _worker;
    private DateTimeOffset _nextRejectLog;

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

        _worker = new InferenceWorker(ProcessYolo);
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

    /// <summary>Capture rate while YOLO runs: enough frames to see a chest closed, then open, in consecutive frames.</summary>
    public const double YoloSamplingFps = 8;

    /// <summary>The model that ships with the app (published next to the executable).</summary>
    public static string BundledModelPath => Path.Combine(AppContext.BaseDirectory, "models", "best.onnx");

    /// <summary>Model used when no path is configured: the bundled one (or the older chest-yolov8.onnx name).</summary>
    public static string DefaultModelPath
    {
        get
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "models", "chest-yolov8.onnx");
            return !File.Exists(BundledModelPath) && File.Exists(legacy) ? legacy : BundledModelPath;
        }
    }

    /// <summary>Last inference time (ms) of the YOLO pipeline, 0 when not running.</summary>
    public double LastInferenceMs { get; private set; }

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
                _yolo.Options = TrackingOptionsFor(_settings);
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
        _worker.Clear();
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
            var roles = info.Labels.Roles;
            ModelDescription = $"{Path.GetFileName(path)} · {info.InputSize} px · {info.Labels.Count} classes ({info.LabelSource}) · {info.Provider}"
                               + (roles.Contains(ObjectRole.ChestOpening) ? "" : " · closed → open counts");
            if (info.ProviderNote is { } note)
            {
                Log.Warn($"YOLO runs on the CPU. {note}");
            }
            // Counting needs the same chest seen closed and then opened; chest_opening is optional.
            ModelError = !roles.Contains(ObjectRole.ChestClosed) || !roles.Contains(ObjectRole.ChestOpened)
                ? $"The model needs chest_closed and chest_opened classes (has: {string.Join(", ", info.Labels.Names)}): nothing will be counted."
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

    private static ChestTrackingOptions TrackingOptionsFor(DetectionSettings s) => YoloSettings.TrackingOptionsFor(s);

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
                source.Start(target, settings.Engine == DetectionEngine.Yolo ? Math.Max(settings.SamplingFps, YoloSamplingFps) : settings.SamplingFps);
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
                // Copy and hand over: the capture buffer is reused, and inference must not block the capture thread.
                _worker.Submit(frame);
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

    /// <summary>Runs on the inference thread, one frame at a time.</summary>
    private void ProcessYolo(CapturedFrame frame)
    {
        YoloChestPipeline? pipeline;
        DetectionSettings settings;
        lock (_gate)
        {
            pipeline = _yolo;
            settings = _settings;
            if (_source is null || settings.Engine != DetectionEngine.Yolo)
            {
                return;
            }
        }

        if (pipeline is null)
        {
            return;
        }

        try
        {
            var result = pipeline.Process(frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.Timestamp);
            LastInferenceMs = result.ElapsedMs;

            // "Candidate": a chest confirmed closed is now seen open, one more open frame will count it.
            var watching = result.Update.Tracks
                .Where(t => t.MissedFrames == 0 && t.State is ChestState.Closed or ChestState.Opening && t.LastRole != ObjectRole.ChestClosed)
                .ToList();
            var best = watching.Select(t => (double)t.LastConfidence).DefaultIfEmpty(0).Max();
            var state = watching.Count > 0 ? DetectorState.Candidate : DetectorState.Idle;
            lock (_gate)
            {
                if (_status is DetectionStatus.Running or DetectionStatus.Candidate)
                {
                    SetStatusLocked(state == DetectorState.Candidate ? DetectionStatus.Candidate : DetectionStatus.Running);
                }
            }

            if (settings.DiagnosticLog)
            {
                LogDiagnostics(result);
            }

            var preview = PreviewRequested ? DebugPreview(frame, result, settings.DebugView) : null;
            var analysis = new FrameAnalysis(new FrameFeatures(frame.Timestamp, 0, 0, 0, null), preview, result.ElapsedMs, frame.Width, frame.Height);
            FrameAnalyzed?.Invoke(this, new FrameAnalyzedEventArgs(analysis, best, state, result));

            foreach (var ev in result.Update.Events)
            {
                Log.Info($"YOLO opening {ev.Outcome} conf={ev.Confidence:0.00} box={ev.Box} {ev.Evidence} ({result.ElapsedMs:0} ms/frame)");
                HandleDecision(ev.ToDecision(), settings, ev.Evidence.ToString(), trackedIdentity: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("YOLO frame analysis failed", ex);
        }
    }

    /// <summary>Optional diagnostics: every state transition, rejected detections at most every 2 s.</summary>
    private void LogDiagnostics(PipelineFrame result)
    {
        foreach (var t in result.Update.Transitions ?? [])
        {
            Log.Info($"YOLO track #{t.TrackId} {t.Transition.From} -> {t.Transition.To} ({t.Transition.Reason}) conf={t.Confidence:0.00} box={t.Box}");
        }

        if (result.Update.Rejected is { Count: > 0 } rejected && result.Timestamp >= _nextRejectLog)
        {
            _nextRejectLog = result.Timestamp.AddSeconds(2);
            Log.Info("YOLO rejected: " + string.Join("; ", rejected.Take(5).Select(r => $"{r.Detection.Label} {r.Detection.Confidence:0.00} {r.Detection.Box}: {r.Reason}")));
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

    private void HandleDecision(DetectionDecision decision, DetectionSettings settings, string? detail = null, bool trackedIdentity = false)
    {
        var result = DetectionCounting.Apply(_engine, decision, settings.TestMode, settings.QueueUncertain, trackedIdentity);

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
        DiagnosticLog = s.DiagnosticLog,
    };

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Dispose();
        StopCapture();
        _worker.Dispose();
        _analyzer.Dispose();
        lock (_gate)
        {
            _yolo?.Dispose();
            _yolo = null;
        }
    }
}

/// <summary>
/// Single background thread for model inference. <see cref="Submit"/> copies the frame into a pending slot and
/// returns at once; if inference is still busy, a newer frame replaces the pending one (frames are dropped, never
/// queued, so detection stays at most one frame behind).
/// </summary>
internal sealed class InferenceWorker : IDisposable
{
    private readonly Action<CapturedFrame> _process;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private byte[] _pendingBuffer = [];
    private byte[] _workBuffer = [];
    private CapturedFrame? _pending;
    private volatile bool _stopped;

    public InferenceWorker(Action<CapturedFrame> process)
    {
        _process = process;
        _thread = new Thread(Run) { IsBackground = true, Name = "YOLO inference", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Frames replaced before they were processed (diagnostics).</summary>
    public long Dropped { get; private set; }

    public void Submit(CapturedFrame frame)
    {
        if (_stopped)
        {
            return;
        }

        var length = frame.Stride * frame.Height;
        lock (_gate)
        {
            if (_pending is not null)
            {
                Dropped++;
            }

            if (_pendingBuffer.Length != length)
            {
                _pendingBuffer = new byte[length];
            }

            Buffer.BlockCopy(frame.Pixels, 0, _pendingBuffer, 0, Math.Min(length, frame.Pixels.Length));
            _pending = new CapturedFrame
            {
                Pixels = _pendingBuffer, Width = frame.Width, Height = frame.Height, Stride = frame.Stride, Timestamp = frame.Timestamp,
            };
        }

        _signal.Set();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }

    private void Run()
    {
        while (!_stopped)
        {
            _signal.WaitOne();
            CapturedFrame? frame;
            lock (_gate)
            {
                frame = _pending;
                _pending = null;
                if (frame is not null)
                {
                    // Swap buffers: the next Submit writes into the other one while this frame is processed.
                    (_pendingBuffer, _workBuffer) = (_workBuffer, _pendingBuffer);
                }
            }

            if (frame is null || _stopped)
            {
                continue;
            }

            try
            {
                _process(frame);
            }
            catch (Exception ex)
            {
                Log.Warn("Inference worker failed", ex);
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }
}
