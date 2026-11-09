using ChestTracker.Core.Tracking;

namespace ChestTracker.Core.Detection;

/// <summary>
/// Turns a finished detection into the normal chest-event pipeline. There is no separate counter for automatic
/// detection: a confirmed opening is one <see cref="TrackingEngine.AddChestAutomatic"/> call, which stores one event
/// with its own id (lifetime, current match, history, undo, sync outbox and party statistics all follow from it).
/// </summary>
public static class DetectionCounting
{
    /// <returns>"counted", "suppressed" (duplicate of a manual add, or cooldown), "review", "test" or "discarded".</returns>
    public static string Apply(TrackingEngine engine, DetectionDecision decision, bool testMode, bool queueUncertain, bool trackedIdentity)
    {
        if (decision.Outcome == DetectionOutcome.Discarded)
        {
            return "discarded";
        }

        if (testMode)
        {
            return "test";
        }

        if (decision.Outcome == DetectionOutcome.Confirmed)
        {
            var r = engine.AddChestAutomatic(decision.Confidence, decision.Timestamp, trackedIdentity);
            return r.Applied ? "counted" : "suppressed";
        }

        if (queueUncertain)
        {
            engine.AddPendingReview(decision.Confidence, decision.Timestamp);
            return "review";
        }

        return "discarded";
    }
}
