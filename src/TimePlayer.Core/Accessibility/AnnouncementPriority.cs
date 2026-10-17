namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Priority level for screen reader live region announcements.
/// Maps to UI Automation live region polite/assertive behaviors.
/// </summary>
public enum AnnouncementPriority
{
    /// <summary>
    /// Polite announcement. Screen reader queues the announcement and speaks it when idle.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority announcement.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// Assertive announcement. Screen reader immediately interrupts current speech to announce this.
    /// Used for critical errors, state changes, or explicit user action confirmations.
    /// </summary>
    High = 2
}
