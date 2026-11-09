using ChestTracker.Core.Tracking;

namespace ChestTracker.Core.Detection;

/// <summary>
/// Turns a finished detection into the normal chest-event pipeline. There is no separate counter for automatic
/// detection: a confirmed opening is one <see cref="TrackingEngine.AddChestAutomatic"/> call, which stores one event
/// with its own id (lifetime, current match, history, undo, sync outbox and party statistics all follow from it).
/// </summary>
public static class DetectionCounting
{
    /// <param name="expectedMatchId">
    /// The local match that was running when the frame was captured. If counting stopped (or a new match started)
    /// in the meantime, the result is "stale" and nothing is stored.
    /// </param>
    /// <returns>"counted", "suppressed" (duplicate of a manual add, or cooldown), "review", "test", "stale" or "discarded".</returns>
    public static string Apply(TrackingEngine engine, DetectionDecision decision, bool testMode, bool queueUncertain, bool trackedIdentity,
        Guid? expectedMatchId = null)
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
            var r = engine.AddChestAutomatic(decision.Confidence, decision.Timestamp, trackedIdentity, expectedMatchId);
            return r.Applied ? "counted"
                : r.Code == TrackingResultCode.NoActiveMatch ? "stale"
                : "suppressed";
        }

        if (queueUncertain)
        {
            return engine.AddPendingReview(decision.Confidence, decision.Timestamp, expectedMatchId) is null ? "stale" : "review";
        }

        return "discarded";
    }
}
