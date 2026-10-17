namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Service contract for dispatching live announcements to assistive technologies (screen readers).
/// </summary>
public interface IAccessibilityService
{
    /// <summary>
    /// Event raised whenever an announcement is dispatched.
    /// UI layers attach to this to trigger UI Automation LiveRegionChanged events.
    /// </summary>
    event EventHandler<AccessibilityAnnouncementEventArgs>? AnnouncementRequested;

    /// <summary>
    /// Dispatches an announcement string to screen readers.
    /// </summary>
    /// <param name="message">Clear, natural speech message.</param>
    /// <param name="priority">Polite, Medium, or Assertive priority.</param>
    void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium);
}
