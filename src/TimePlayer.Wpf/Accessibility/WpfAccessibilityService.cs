using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;

namespace TimePlayer.Wpf.Accessibility;

/// <summary>
/// WPF implementation of IAccessibilityService.
/// Dispatches announcements to UI Automation live regions and raises LiveRegionChanged events.
/// Inspired by the trypsynth live-region pattern.
/// </summary>
public sealed class WpfAccessibilityService : IAccessibilityService
{
    private readonly ILogger<WpfAccessibilityService> _logger;
    private TextBlock? _liveRegionControl;
    private Dispatcher? _dispatcher;

    public event EventHandler<AccessibilityAnnouncementEventArgs>? AnnouncementRequested;

    public WpfAccessibilityService(ILogger<WpfAccessibilityService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers the WPF TextBlock designated as the UI Automation live region.
    /// </summary>
    public void RegisterLiveRegion(TextBlock liveRegionControl)
    {
        _liveRegionControl = liveRegionControl ?? throw new ArgumentNullException(nameof(liveRegionControl));
        _dispatcher = liveRegionControl.Dispatcher;
    }

    public void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _logger.LogInformation("[Accessibility Announcement] ({Priority}): {Message}", priority, message);

        var args = new AccessibilityAnnouncementEventArgs(message, priority);
        AnnouncementRequested?.Invoke(this, args);

        if (_liveRegionControl != null && _dispatcher != null)
        {
            if (_dispatcher.CheckAccess())
            {
                UpdateLiveRegion(message);
            }
            else
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => UpdateLiveRegion(message));
            }
        }
    }

    private void UpdateLiveRegion(string message)
    {
        if (_liveRegionControl == null) return;

        // Updating the text on a live region informs screen readers that new content has arrived
        _liveRegionControl.Text = message;

        // Explicitly raise the UI Automation LiveRegionChanged event on the element's automation peer
        var peer = UIElementAutomationPeer.FromElement(_liveRegionControl);
        if (peer == null)
        {
            peer = UIElementAutomationPeer.CreatePeerForElement(_liveRegionControl);
        }

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
