using System.Windows;
using System.Windows.Automation;
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
        _liveRegionControl.ClearValue(System.Windows.Automation.AutomationProperties.NameProperty);
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

    private bool? _captionPathLogged;

    // Captions are spoken from a background worker that always takes the newest caption, so the UI
    // thread never waits on NVDA and a slow moment never builds a backlog.
    private string? _pendingCaption;
    private readonly SemaphoreSlim _captionSignal = new(0);
    private Task? _captionWorker;

    public void AnnounceCaption(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        AnnouncementRequested?.Invoke(this, new AccessibilityAnnouncementEventArgs(text, AnnouncementPriority.Low));
        Interlocked.Exchange(ref _pendingCaption, text);
        _captionWorker ??= Task.Run(CaptionLoopAsync);
        if (_captionSignal.CurrentCount == 0)
            _captionSignal.Release();
    }

    private async Task CaptionLoopAsync()
    {
        while (true)
        {
            await _captionSignal.WaitAsync();
            var text = Interlocked.Exchange(ref _pendingCaption, null);
            if (text is null)
                continue;
            try
            {
                SpeakCaption(text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not speak a caption.");
            }
        }
    }

    private void SpeakCaption(string text)
    {
        // NVDA: cancel the previous caption and speak this one now.
        bool viaNvda = NvdaBrailleBridge.SpeakInterrupting(text);
        if (_captionPathLogged != viaNvda)
        {
            _captionPathLogged = viaNvda;
            _logger.LogInformation("Captions are spoken {Path}.", viaNvda ? "through NVDA, interrupting the previous caption" : "with UI Automation notifications (most recent only)");
        }
        if (viaNvda)
            return;

        // Other screen readers: a notification that replaces any still-pending caption.
        if (_liveRegionControl is null || _dispatcher is null)
            return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            var peer = UIElementAutomationPeer.FromElement(_liveRegionControl)
                       ?? UIElementAutomationPeer.CreatePeerForElement(_liveRegionControl);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "TimePlayerCaption");
        });
    }

    private void UpdateLiveRegion(string message)
    {
        if (_liveRegionControl == null) return;

        // Ensure AutomationProperties.Name is cleared so the screen reader reads the message text directly
        _liveRegionControl.ClearValue(System.Windows.Automation.AutomationProperties.NameProperty);
        _liveRegionControl.Text = message;

        // Explicitly raise the UI Automation LiveRegionChanged event on the element's automation peer
        var peer = UIElementAutomationPeer.FromElement(_liveRegionControl)
                   ?? UIElementAutomationPeer.CreatePeerForElement(_liveRegionControl);

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
