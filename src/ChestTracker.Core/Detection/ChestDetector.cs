namespace ChestTracker.Core.Detection;

/// <summary>
/// Visual measurements of one captured frame, taken inside the region of interest around the crosshair.
/// </summary>
/// <param name="GoldRatio">Fraction (0..1) of ROI pixels inside the calibrated gold HSV range.</param>
/// <param name="Brightness">Mean luminance of the ROI (0..255).</param>
/// <param name="Motion">Mean absolute difference to the previous frame (0..1).</param>
/// <param name="TemplateScore">Normalized correlation with the calibrated prompt template (−1..1), null when unused.</param>
public readonly record struct FrameFeatures(
    DateTimeOffset Timestamp,
    double GoldRatio,
    double Brightness,
    double Motion,
    double? TemplateScore);

public enum DetectorState
{
    WarmingUp,
    Idle,
    Candidate,
    Cooldown,
}

public enum DetectionOutcome
{
    /// <summary>Confidence above the confirm threshold: counted.</summary>
    Confirmed,

    /// <summary>Between review and confirm thresholds: queued for user review.</summary>
    Uncertain,

    /// <summary>A candidate that did not reach the review threshold.</summary>
    Discarded,
}

public sealed record DetectionDecision(DetectionOutcome Outcome, double Confidence, DateTimeOffset Timestamp, int Frames);

/// <summary>Thresholds derived from user settings.</summary>
public sealed record DetectionTuning
{
    public double ConfirmThreshold { get; init; } = 0.65;
    public double ReviewThreshold { get; init; } = 0.45;
    public double CandidateThreshold { get; init; } = 0.38;

    /// <summary>Minimum consecutive frames above the candidate threshold.</summary>
    public int MinFrames { get; init; } = 2;

    /// <summary>A candidate longer than this is finalized anyway (e.g. standing in front of a golden wall).</summary>
    public TimeSpan MaxEventDuration { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Gold ratio increase over the baseline that maps to full gold evidence.</summary>
    public double GoldSpikeReference { get; init; } = 0.06;

    /// <summary>Brightness increase over the baseline that maps to full brightness evidence.</summary>
    public double BrightnessReference { get; init; } = 35;

    /// <summary>
    /// Minimum gold evidence (0..1) for any positive outcome. Ammo boxes and other containers
    /// produce brightness/motion/prompt evidence but little gold, so they are rejected here.
    /// </summary>
    public double MinGoldEvidence { get; init; } = 0.3;

    public int WarmupFrames { get; init; } = 3;

    /// <summary>Exponential moving average factor for the idle baseline.</summary>
    public double BaselineAlpha { get; init; } = 0.08;

    /// <summary>A gap between frames longer than this resets the candidate (capture paused).</summary>
    public TimeSpan MaxFrameGap { get; init; } = TimeSpan.FromSeconds(3);

    /// <param name="sensitivity">0 (strict) .. 100 (lenient).</param>
    public static DetectionTuning FromSensitivity(int sensitivity, double cooldownSeconds, double samplingFps)
    {
        var s = Math.Clamp(sensitivity, 0, 100) / 100.0;
        var confirm = 0.82 - (0.32 * s);
        var review = confirm - 0.18;
        return new DetectionTuning
        {
            ConfirmThreshold = confirm,
            ReviewThreshold = review,
            CandidateThreshold = review * 0.85,
            MinFrames = samplingFps >= 4 ? 2 : 1,
            Cooldown = TimeSpan.FromSeconds(Math.Clamp(cooldownSeconds, 0.5, 30)),
        };
    }
}

/// <summary>
/// EXPERIMENTAL. Temporal state machine that turns per-frame features into chest-opening decisions.
/// A treasure chest opening produces a short golden burst around the crosshair; the detector looks
/// for a gold spike relative to a slowly adapting baseline that persists for a few frames, then applies
/// confidence thresholds and a cooldown. It is a heuristic and will make mistakes.
/// </summary>
public sealed class ChestDetector
{
    private DetectionTuning _tuning;
    private int _warmupSeen;
    private DateTimeOffset? _lastFrame;
    private DateTimeOffset _candidateStart;
    private DateTimeOffset _cooldownUntil;
    private int _candidateFrames;
    private double _candidatePeak;
    private double _candidateSum;
    private double _candidateGoldPeak;

    public ChestDetector(DetectionTuning tuning) => _tuning = tuning;

    public DetectorState State { get; private set; } = DetectorState.WarmingUp;
    public double BaselineGold { get; private set; }
    public double BaselineBrightness { get; private set; }
    public double LastConfidence { get; private set; }
    public double LastGoldEvidence { get; private set; }

    public DetectionTuning Tuning
    {
        get => _tuning;
        set => _tuning = value;
    }

    public void Reset()
    {
        State = DetectorState.WarmingUp;
        _warmupSeen = 0;
        _lastFrame = null;
        _candidateFrames = 0;
        LastConfidence = 0;
        LastGoldEvidence = 0;
    }

    /// <summary>Instantaneous confidence (0..1) of a frame against the current baseline.</summary>
    public double Score(in FrameFeatures f, out double goldEvidence)
    {
        goldEvidence = Clamp01((f.GoldRatio - BaselineGold) / _tuning.GoldSpikeReference);
        var brightEvidence = Clamp01((f.Brightness - BaselineBrightness) / _tuning.BrightnessReference);
        var motionEvidence = Clamp01(f.Motion / 0.12);

        if (f.TemplateScore is { } t)
        {
            var templateEvidence = Clamp01((t - 0.45) / 0.4);
            return (0.50 * goldEvidence) + (0.15 * brightEvidence) + (0.10 * motionEvidence) + (0.25 * templateEvidence);
        }

        return (0.65 * goldEvidence) + (0.20 * brightEvidence) + (0.15 * motionEvidence);
    }

    /// <summary>Processes a frame; returns a decision when a candidate event finishes.</summary>
    public DetectionDecision? Process(in FrameFeatures f)
    {
        if (_lastFrame is { } last && (f.Timestamp - last > _tuning.MaxFrameGap || f.Timestamp < last))
        {
            // Capture was paused or the clock jumped: drop any half-seen event.
            if (State == DetectorState.Candidate)
            {
                State = DetectorState.Idle;
            }
        }

        _lastFrame = f.Timestamp;

        if (State == DetectorState.WarmingUp)
        {
            UpdateBaseline(f, _warmupSeen == 0 ? 1.0 : 0.5);
            LastConfidence = 0;
            if (++_warmupSeen >= _tuning.WarmupFrames)
            {
                State = DetectorState.Idle;
            }

            return null;
        }

        var score = Score(f, out var gold);
        LastConfidence = score;
        LastGoldEvidence = gold;

        switch (State)
        {
            case DetectorState.Cooldown:
                if (f.Timestamp >= _cooldownUntil)
                {
                    State = DetectorState.Idle;
                    goto case DetectorState.Idle;
                }

                return null;

            case DetectorState.Idle:
                if (score >= _tuning.CandidateThreshold && gold >= _tuning.MinGoldEvidence)
                {
                    State = DetectorState.Candidate;
                    _candidateStart = f.Timestamp;
                    _candidateFrames = 1;
                    _candidatePeak = score;
                    _candidateSum = score;
                    _candidateGoldPeak = gold;
                    return null;
                }

                UpdateBaseline(f, _tuning.BaselineAlpha);
                return null;

            case DetectorState.Candidate:
                var stillActive = score >= _tuning.CandidateThreshold * 0.8;
                if (stillActive)
                {
                    _candidateFrames++;
                    _candidatePeak = Math.Max(_candidatePeak, score);
                    _candidateSum += score;
                    _candidateGoldPeak = Math.Max(_candidateGoldPeak, gold);
                    if (f.Timestamp - _candidateStart >= _tuning.MaxEventDuration)
                    {
                        return Finalize(f.Timestamp);
                    }

                    return null;
                }

                return Finalize(f.Timestamp);

            default:
                return null;
        }
    }

    private DetectionDecision Finalize(DateTimeOffset now)
    {
        var frames = _candidateFrames;
        var confidence = frames == 0 ? 0 : (0.6 * _candidatePeak) + (0.4 * (_candidateSum / frames));
        _candidateFrames = 0;

        DetectionOutcome outcome;
        if (frames < _tuning.MinFrames || _candidateGoldPeak < _tuning.MinGoldEvidence)
        {
            outcome = DetectionOutcome.Discarded;
        }
        else if (confidence >= _tuning.ConfirmThreshold)
        {
            outcome = DetectionOutcome.Confirmed;
        }
        else if (confidence >= _tuning.ReviewThreshold)
        {
            outcome = DetectionOutcome.Uncertain;
        }
        else
        {
            outcome = DetectionOutcome.Discarded;
        }

        if (outcome == DetectionOutcome.Discarded)
        {
            State = DetectorState.Idle;
        }
        else
        {
            State = DetectorState.Cooldown;
            _cooldownUntil = _candidateStart + _tuning.Cooldown;
        }

        return new DetectionDecision(outcome, Math.Round(confidence, 3), _candidateStart, frames);
    }

    private void UpdateBaseline(in FrameFeatures f, double alpha)
    {
        BaselineGold += alpha * (f.GoldRatio - BaselineGold);
        BaselineBrightness += alpha * (f.Brightness - BaselineBrightness);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
