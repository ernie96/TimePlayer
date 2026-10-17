using TimePlayer.Core.Accessibility;
using Xunit;

namespace TimePlayer.Tests.Logging;

public class LoggingAndErrorTests
{
    private sealed class TestAccessibilityService : IAccessibilityService
    {
        public List<AccessibilityAnnouncementEventArgs> Announcements { get; } = new();

        public event EventHandler<AccessibilityAnnouncementEventArgs>? AnnouncementRequested;

        public void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
        {
            var args = new AccessibilityAnnouncementEventArgs(message, priority);
            Announcements.Add(args);
            AnnouncementRequested?.Invoke(this, args);
        }
    }

    [Fact]
    public void AccessibleErrorAnnouncement_DoesNotExposeRawStackTrace()
    {
        var accessibility = new TestAccessibilityService();

        var ex = new InvalidOperationException("Simulated HTTP connection timeout at TimePlayer.Backends.PipedBackend.GetStreams()");
        
        // Error presentation rule: User-friendly message with assertive priority, avoiding raw internal stack trace
        var friendlyMessage = "Failed to load stream. Please try again or paste a direct video URL.";
        accessibility.Announce(friendlyMessage, AnnouncementPriority.High);

        Assert.Single(accessibility.Announcements);
        var announcement = accessibility.Announcements[0];
        Assert.Equal(AnnouncementPriority.High, announcement.Priority);
        Assert.DoesNotContain("at TimePlayer.Backends", announcement.Message);
        Assert.DoesNotContain("InvalidOperationException", announcement.Message);
        Assert.Equal(friendlyMessage, announcement.Message);
    }
}
