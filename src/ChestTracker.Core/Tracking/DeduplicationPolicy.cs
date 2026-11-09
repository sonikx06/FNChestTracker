using ChestTracker.Core.Models;

namespace ChestTracker.Core.Tracking;

/// <summary>
/// Decides whether an incoming chest signal is a new chest or a duplicate.
/// Rules:
/// <list type="bullet">
/// <item>Hotkey presses closer than <see cref="TrackingOptions.ManualDebounce"/> are ignored (accidental double press).</item>
/// <item>Automatic detections closer than <see cref="TrackingOptions.AutomaticCooldown"/> are ignored (same animation seen twice),
/// except detections from a per-chest tracker (YOLO): each tracked chest counts once, so different chests opened in
/// quick succession all count.</item>
/// <item>Outside manual mode a hotkey add right after an automatic add (or an automatic add right after a manual add)
/// within <see cref="TrackingOptions.HybridMergeWindow"/> is merged into one chest. The merge is consumed, so a
/// second hotkey press counts normally.</item>
/// </list>
/// UI button clicks are deliberate and are never debounced or merged.
/// </summary>
public sealed class DeduplicationPolicy
{
    public DateTimeOffset? LastManualAdd { get; private set; }
    public DateTimeOffset? LastAddHotkey { get; private set; }
    public DateTimeOffset? LastRemoveHotkey { get; private set; }
    public DateTimeOffset? LastAutomaticAdd { get; private set; }

    /// <param name="trackedIdentity">The automatic detection comes from a tracker that already guarantees one event per chest.</param>
    public TrackingResultCode CheckAdd(EventSource source, bool fromHotkey, TrackingMode mode, DateTimeOffset now, TrackingOptions options,
        bool trackedIdentity = false)
    {
        if (source == EventSource.Manual)
        {
            if (!fromHotkey)
            {
                return TrackingResultCode.Applied;
            }

            if (Within(LastAddHotkey, now, options.ManualDebounce))
            {
                return TrackingResultCode.IgnoredDebounce;
            }

            if (mode != TrackingMode.Manual && Within(LastAutomaticAdd, now, options.HybridMergeWindow))
            {
                // The press confirms the automatic detection of the same chest.
                LastAutomaticAdd = null;
                LastAddHotkey = now;
                return TrackingResultCode.IgnoredDuplicate;
            }

            return TrackingResultCode.Applied;
        }

        if (!trackedIdentity && Within(LastAutomaticAdd, now, options.AutomaticCooldown))
        {
            return TrackingResultCode.IgnoredCooldown;
        }

        if (Within(LastManualAdd, now, options.HybridMergeWindow))
        {
            // Already counted by hand; start the cooldown so the rest of the animation is ignored too.
            LastManualAdd = null;
            LastAutomaticAdd = now;
            return TrackingResultCode.IgnoredDuplicate;
        }

        return TrackingResultCode.Applied;
    }

    public void RecordAdd(EventSource source, bool fromHotkey, DateTimeOffset now)
    {
        if (source == EventSource.Manual)
        {
            LastManualAdd = now;
            if (fromHotkey)
            {
                LastAddHotkey = now;
            }
        }
        else
        {
            LastAutomaticAdd = now;
        }
    }

    public bool CheckRemoveHotkey(DateTimeOffset now, TrackingOptions options) =>
        !Within(LastRemoveHotkey, now, options.ManualDebounce);

    public void RecordRemoveHotkey(DateTimeOffset now) => LastRemoveHotkey = now;

    public void Reset()
    {
        LastManualAdd = null;
        LastAddHotkey = null;
        LastRemoveHotkey = null;
        LastAutomaticAdd = null;
    }

    private static bool Within(DateTimeOffset? last, DateTimeOffset now, TimeSpan window)
    {
        if (last is null || window <= TimeSpan.Zero)
        {
            return false;
        }

        var elapsed = now - last.Value;
        return elapsed >= TimeSpan.Zero && elapsed < window;
    }
}
