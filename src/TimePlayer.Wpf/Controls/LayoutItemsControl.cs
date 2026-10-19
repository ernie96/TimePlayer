using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace TimePlayer.Wpf.Controls;

/// <summary>
/// An ItemsControl used only to lay out a repeated group of controls (settings rows).
/// A plain ItemsControl exposes each row to screen readers as a "data item" named after the row's
/// class, which they read before the real control. This one exposes just the controls inside.
/// </summary>
public sealed class LayoutItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LayoutPeer(this);

    private sealed class LayoutPeer : FrameworkElementAutomationPeer
    {
        public LayoutPeer(LayoutItemsControl owner) : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        // A layout group, not something to stop on: the controls inside are what matter.
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
