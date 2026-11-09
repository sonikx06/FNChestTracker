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

    /// <summary>Automatic/hybrid mode, but no match is running: nothing is captured or counted.</summary>
    Paused,
}

/// <param name="Detail">Supporting evidence (frames per state, confidences, state path).</param>
public sealed record DetectionLogEntry(DateTimeOffset Timestamp, DetectionOutcome Outcome, double Confidence, string Result, string? Detail = null);

public sealed class FrameAnalyzedEventArgs(FrameAnalysis analysis, double confidence, DetectorState state, PipelineFrame? yolo = null) : EventArgs
{
    public FrameAnalysis Analysis { get; } = analysis;
    public double Confidence { get; } = confidence;
    public DetectorState State { get; } = state;

    /// <summary>The model's detections, tracks and openings for this frame.</summary>
    public PipelineFrame? Yolo { get; } = yolo;
}

/// <summary>
/// Automatic chest detection with the built-in YOLOv8 model (<c>models\best.onnx</c>), the only detector.
/// Capture and inference run only while the tracking mode is automatic or hybrid AND a match is running
/// (<see cref="MatchDetectionController"/>): the match button and the match hotkey start and stop counting.
/// Frames go to a dedicated inference thread (latest frame wins), so neither the capture callback nor the UI thread
/// waits for the model. If capture or the model fails, the status says so and manual tracking keeps working.
/// </summary>
public sealed class DetectionService : IDisposable
{
    private readonly TrackingEngine _engine;
    private readonly MatchDetectionController _controller;
    private readonly object _gate = new();
    private readonly Timer _watchdog;
    private readonly InferenceWorker _worker;
    private readonly string _modelPath;
    private ICaptureSource? _source;
    private DetectionSettings _settings;
    private TrackingMode _mode;
    private DetectionStatus _status = DetectionStatus.Disabled;
    private DateTimeOffset _detectedUntil;
    private bool _disposed;
    private bool _modelAttempted;
    private DateTimeOffset _nextRejectLog;

    /// <param name="modelPath">Built-in model; tests and tools may pass another file, the app never does.</param>
    public DetectionService(TrackingEngine engine, DetectionSettings settings, TrackingMode mode, string? modelPath = null)
    {
        _engine = engine;
        _settings = Clone(settings);
        _mode = mode;
        _modelPath = modelPath ?? BuiltInModelPath;
        _controller = new MatchDetectionController(engine);
        _controller.CountingChanged += (_, _) => ThreadPool.QueueUserWorkItem(_ => OnCountingChanged());
        _worker = new InferenceWorker(ProcessFrame);
        IsCaptureSupported = WgcCaptureSource.IsSupported();
        _watchdog = new Timer(_ => Watchdog(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    public event EventHandler? StatusChanged;

    /// <summary>Raised on the inference thread for every analysed frame (detection page).</summary>
    public event EventHandler<FrameAnalyzedEventArgs>? FrameAnalyzed;

    /// <summary>Raised on the inference thread for every finished opening.</summary>
    public event EventHandler<DetectionLogEntry>? Decision;

    /// <summary>Capture rate while counting: enough frames to see a chest closed, then open, in consecutive frames.</summary>
    public const double SamplingFps = 8;

    /// <summary>The model that ships with the app (published next to the executable). There is no other.</summary>
    public static string BuiltInModelPath => BuiltInModel.PathIn(AppContext.BaseDirectory);

    public bool IsCaptureSupported { get; }

    /// <summary>Set while the detection page is visible; previews cost extra CPU.</summary>
    public bool PreviewRequested { get; set; }

    public string? LastError { get; private set; }

    /// <summary>Why the built-in model is not available (missing or damaged file), null when it is loaded.</summary>
    public string? ModelError { get; private set; }

    /// <summary>Loaded model description, e.g. "best.onnx · 640 px · closed_chest, open_chest · DirectML (GPU)".</summary>
    public string? ModelDescription { get; private set; }

    /// <summary>Last inference time (ms), 0 when not running.</summary>
    public double LastInferenceMs { get; private set; }

    public string? TargetDescription { get; private set; }

    public DetectionStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsActiveMode => _mode is TrackingMode.Automatic or TrackingMode.Hybrid;

    /// <summary>True while a match is running and detected openings are counted.</summary>
    public bool IsCounting => _controller.IsCounting;

    public void Configure(DetectionSettings settings, TrackingMode mode)
    {
        bool restart;
        lock (_gate)
        {
            var old = _settings;
            _settings = Clone(settings);
            restart = old.Backend != _settings.Backend || old.Target != _settings.Target
                      || !string.Equals(old.ProcessName, _settings.ProcessName, StringComparison.OrdinalIgnoreCase)
                      || _mode != mode;
            _mode = mode;
            if (_controller.Pipeline is { Detector: OnnxYoloDetector onnx } pipeline)
            {
                onnx.ConfidenceThreshold = (float)_settings.YoloConfidence;
                pipeline.Options = YoloSettings.TrackingOptionsFor(_settings);
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
        _controller.Pipeline?.Reset();
        Watchdog();
    }

    /// <summary>Counting started or stopped (match button, match hotkey, shared match, crash recovery).</summary>
    private void OnCountingChanged()
    {
        _worker.Clear();
        if (_controller.IsCounting)
        {
            Log.Info("Counting started: chest detection active for the new match");
            Watchdog();
        }
        else
        {
            StopCapture();
            lock (_gate)
            {
                SetStatusLocked(IsActiveMode ? DetectionStatus.Paused : DetectionStatus.Disabled);
            }

            Log.Info("Counting stopped: chest detection paused until the next match");
        }
    }

    /// <summary>Loads the built-in model once (at the first need) and keeps it for every match.</summary>
    private void EnsureModelLocked(DetectionSettings settings)
    {
        if (_controller.Pipeline is not null || _modelAttempted)
        {
            return;
        }

        _modelAttempted = true;
        try
        {
            var detector = BuiltInModel.Load(_modelPath, threads: Math.Max(1, Environment.ProcessorCount / 4));
            detector.ConfidenceThreshold = (float)settings.YoloConfidence;
            _controller.Pipeline = new YoloChestPipeline(detector, YoloSettings.TrackingOptionsFor(settings));
            var info = detector.Info;
            ModelDescription = $"{Path.GetFileName(_modelPath)} · {info.InputSize} px · {string.Join(", ", info.Labels.Names)} · {info.Provider}";
            ModelError = null;
            if (info.ProviderNote is { } note)
            {
                Log.Warn($"Chest model runs on the CPU. {note}");
            }

            Log.Info($"Built-in chest model loaded: {ModelDescription}");
        }
        catch (BuiltInModelException ex)
        {
            ModelError = ex.Message;
            ModelDescription = null;
            Log.Warn("Built-in chest model unavailable; automatic detection is off, manual tracking continues", ex);
        }
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

                // The model is loaded as soon as automatic detection is possible, so a missing or damaged file is
                // reported right away and the first match does not wait for it.
                EnsureModelLocked(settings);
                if (_controller.Pipeline is null)
                {
                    StopCaptureLocked();
                    LastError = ModelError;
                    SetStatusLocked(DetectionStatus.Error);
                    return;
                }

                if (!_controller.IsCounting)
                {
                    StopCaptureLocked();
                    SetStatusLocked(DetectionStatus.Paused);
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
                source.Start(target, SamplingFps);
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
                if (!IsActiveMode || _disposed || !_controller.IsCounting || _source is not null)
                {
                    source.FrameCaptured -= OnFrame;
                    source.Failed -= OnSourceFailed;
                    source.Dispose();
                    return;
                }

                _source = source;
                TargetDescription = target.Description;
                LastError = null;
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
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _source))
            {
                return;
            }
        }

        // The ticket records which match this frame belongs to; no match → the frame is not analysed at all.
        if (_controller.Admit() is { } ticket)
        {
            _worker.Submit(frame, ticket);
        }
    }

    /// <summary>Runs on the inference thread, one frame at a time.</summary>
    private void ProcessFrame(CapturedFrame frame, FrameTicket ticket)
    {
        DetectionSettings settings;
        lock (_gate)
        {
            settings = _settings;
        }

        try
        {
            var outcome = _controller.Process(ticket, frame.Pixels, frame.Width, frame.Height, frame.Stride, frame.Timestamp,
                (decision, matchId) => DetectionCounting.Apply(_engine, decision, settings.TestMode, settings.QueueUncertain,
                    trackedIdentity: true, expectedMatchId: matchId));
            if (outcome is null)
            {
                return; // counting stopped while this frame was in flight
            }

            var result = outcome.Frame;
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

            foreach (var (ev, applied) in outcome.Openings)
            {
                Log.Info($"YOLO opening {ev.Outcome} conf={ev.Confidence:0.00} box={ev.Box} {ev.Evidence} ({result.ElapsedMs:0} ms/frame) -> {applied}");
                Report(ev.ToDecision(), applied, ev.Evidence.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Frame analysis failed", ex);
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

    private void Report(DetectionDecision decision, string result, string? detail)
    {
        if (result == "counted")
        {
            lock (_gate)
            {
                _detectedUntil = DateTimeOffset.UtcNow.AddSeconds(2);
                SetStatusLocked(DetectionStatus.Detected);
            }
        }

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

    private static DetectionSettings Clone(DetectionSettings s) => new()
    {
        Sensitivity = s.Sensitivity, Backend = s.Backend, Target = s.Target, ProcessName = s.ProcessName, TestMode = s.TestMode,
        QueueUncertain = s.QueueUncertain, YoloConfidence = s.YoloConfidence, DebugView = s.DebugView, DiagnosticLog = s.DiagnosticLog,
    };

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Dispose();
        StopCapture();
        _worker.Dispose();
        _controller.Dispose();
        lock (_gate)
        {
            _controller.Pipeline?.Dispose();
            _controller.Pipeline = null;
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
    private readonly Action<CapturedFrame, FrameTicket> _process;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private byte[] _pendingBuffer = [];
    private byte[] _workBuffer = [];
    private CapturedFrame? _pending;
    private FrameTicket _pendingTicket;
    private volatile bool _stopped;

    public InferenceWorker(Action<CapturedFrame, FrameTicket> process)
    {
        _process = process;
        _thread = new Thread(Run) { IsBackground = true, Name = "YOLO inference", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Frames replaced before they were processed (diagnostics).</summary>
    public long Dropped { get; private set; }

    public void Submit(CapturedFrame frame, FrameTicket ticket)
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
            _pendingTicket = ticket;
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
            FrameTicket ticket;
            lock (_gate)
            {
                frame = _pending;
                ticket = _pendingTicket;
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
                _process(frame, ticket);
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
