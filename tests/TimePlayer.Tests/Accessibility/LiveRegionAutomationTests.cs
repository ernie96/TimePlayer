using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using TimePlayer.Wpf.Accessibility;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

public class LiveRegionAutomationTests
{
    private static void RunOnStaThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    [Fact]
    public void LiveRegion_AutomationName_ReturnsActualMessageText_NeverScreenReaderAnnouncements()
    {
        RunOnStaThread(() =>
        {
            var textBlock = new TextBlock();
            var service = new WpfAccessibilityService(NullLogger<WpfAccessibilityService>.Instance);
            service.RegisterLiveRegion(textBlock);

            // Announce a playback message
            service.Announce("Playing");

            var peer = UIElementAutomationPeer.CreatePeerForElement(textBlock);
            var accessibleName = peer.GetName();

            Assert.Equal("Playing", accessibleName);
            Assert.NotEqual("Screen reader announcements", accessibleName);
        });
    }

    [Fact]
    public void LiveRegion_ClearsStaticNameProperty_WhenAnnouncing()
    {
        RunOnStaThread(() =>
        {
            var textBlock = new TextBlock();
            // Simulate accidental static name
            AutomationProperties.SetName(textBlock, "Screen reader announcements");

            var service = new WpfAccessibilityService(NullLogger<WpfAccessibilityService>.Instance);
            service.RegisterLiveRegion(textBlock);

            service.Announce("Volume 85 percent");

            var peer = UIElementAutomationPeer.CreatePeerForElement(textBlock);
            var accessibleName = peer.GetName();

            // Must announce the actual volume, NOT the static label
            Assert.Equal("Volume 85 percent", accessibleName);
            Assert.NotEqual("Screen reader announcements", accessibleName);
        });
    }
}
