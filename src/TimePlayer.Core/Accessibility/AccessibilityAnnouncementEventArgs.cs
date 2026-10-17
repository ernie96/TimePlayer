namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Event arguments for screen reader announcement requests.
/// </summary>
public sealed class AccessibilityAnnouncementEventArgs : EventArgs
{
    public string Message { get; }
    public AnnouncementPriority Priority { get; }
    public DateTimeOffset Timestamp { get; }

    public AccessibilityAnnouncementEventArgs(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Priority = priority;
        Timestamp = DateTimeOffset.UtcNow;
    }
}
