using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;

namespace AccessibleYoutubePlayer.Wpf.Services;

/// <summary>
/// WPF implementation of accessibility announcements.
/// Updates the live region and raises UI Automation LiveRegionChanged notification events
/// so screen readers like NVDA, JAWS, and Narrator announce events promptly.
/// </summary>
public class WpfAccessibilityService : IAccessibilityService
{
    private TextBlock? _announcementBlock;

    public void RegisterAnnouncementElement(TextBlock element)
    {
        _announcementBlock = element;
        AutomationProperties.SetLiveSetting(_announcementBlock, AutomationLiveSetting.Polite);
    }

    public void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_announcementBlock == null) return;

            var liveSetting = priority switch
            {
                AnnouncementPriority.High => AutomationLiveSetting.Assertive,
                _ => AutomationLiveSetting.Polite
            };

            AutomationProperties.SetLiveSetting(_announcementBlock, liveSetting);
            _announcementBlock.Text = message;

            // Raise UI Automation live region event
            var peer = UIElementAutomationPeer.FromElement(_announcementBlock)
                       ?? UIElementAutomationPeer.CreatePeerForElement(_announcementBlock);

            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }
}
