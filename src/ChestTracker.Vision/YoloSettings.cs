using ChestTracker.Core.Settings;

namespace ChestTracker.Vision;

/// <summary>Maps the user's detection settings to tracker options (used by the app and by the tests).</summary>
public static class YoloSettings
{
    /// <summary>
    /// Per-class floors stay at the conservative defaults unless the user raises the confidence slider; the
    /// sensitivity slider moves the auto-count / review thresholds.
    /// </summary>
    public static ChestTrackingOptions TrackingOptionsFor(DetectionSettings s)
    {
        var (confirm, review) = Thresholds(s.Sensitivity);
        var floor = (float)s.YoloConfidence;
        var defaults = new ChestTrackingOptions();
        return defaults with
        {
            MinDetectionConfidence = floor,
            MinClosedConfidence = Math.Max(defaults.MinClosedConfidence, floor),
            MinOpenedConfidence = Math.Max(defaults.MinOpenedConfidence, floor),
            ConfirmThreshold = confirm,
            ReviewThreshold = review,
        };
    }

    /// <summary>
    /// Auto-count and review thresholds for the mean open-chest probability of a confirmed transition.
    /// Sensitivity 50 → count at ≥ 0.55, review at ≥ 0.40; higher sensitivity counts more readily.
    /// </summary>
    public static (double Confirm, double Review) Thresholds(int sensitivity)
    {
        var s = Math.Clamp(sensitivity, 0, 100) / 100.0;
        var confirm = 0.65 - (0.20 * s);
        return (confirm, confirm - 0.15);
    }
}
