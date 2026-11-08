namespace ChestTracker.Core.Models;

/// <summary>How chests are counted during a match.</summary>
public enum TrackingMode
{
    /// <summary>Only global hotkeys / UI buttons count chests.</summary>
    Manual = 0,

    /// <summary>Experimental screen analysis counts chests.</summary>
    Automatic = 1,

    /// <summary>Screen analysis counts, hotkeys correct.</summary>
    Hybrid = 2,
}

/// <summary>Who produced an event.</summary>
public enum EventSource
{
    Manual = 0,
    Automatic = 1,
}

/// <summary>What an event does to the counter.</summary>
public enum ChestEventKind
{
    /// <summary>+1 chest.</summary>
    Add = 0,

    /// <summary>Compensates a specific earlier <see cref="Add"/> event (-1).</summary>
    Undo = 1,

    /// <summary>Arbitrary delta produced by manual editing.</summary>
    Correction = 2,
}

public enum ReviewStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}
