using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using TimePlayer.Wpf.Controls;
using Xunit;

namespace TimePlayer.Tests.ViewModels;

public class LayoutItemsControlTests
{
    private sealed record Row(string Name);

    /// <summary>Screen readers read "data item" plus the row's class name before each setting; the rows must not be exposed.</summary>
    [Fact]
    public void Rows_AreNotExposedAsDataItems_OnlyTheControlsInside()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var template = (DataTemplate)XamlReader.Parse(
                    """
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                        <StackPanel Orientation="Horizontal">
                            <ComboBox AutomationProperties.Name="{Binding Name}"/>
                        </StackPanel>
                    </DataTemplate>
                    """);
                var list = new LayoutItemsControl { ItemsSource = new[] { new Row("Sponsor"), new Row("Intro") }, ItemTemplate = template };
                var plain = new ItemsControl { ItemsSource = new[] { new Row("Sponsor") }, ItemTemplate = template };
                var panel = new StackPanel();
                panel.Children.Add(list);
                panel.Children.Add(plain);
                var window = new Window { Content = panel, Left = -20000, Top = -20000, Width = 400, Height = 400, ShowActivated = false, ShowInTaskbar = false };
                window.Show();

                var peer = UIElementAutomationPeer.CreatePeerForElement(list);
                var children = peer.GetChildren() ?? new List<AutomationPeer>();

                Assert.DoesNotContain(children, c => c.GetAutomationControlType() == AutomationControlType.DataItem);
                Assert.Equal(new[] { "Sponsor", "Intro" }, children.Select(c => c.GetName()));
                Assert.All(children, c => Assert.Equal(AutomationControlType.ComboBox, c.GetAutomationControlType()));
                Assert.False(peer.IsControlElement());

                // A plain ItemsControl is what read "TimePlayer.Wpf.ViewModels.SegmentCategorySetting, data item".
                var plainChildren = UIElementAutomationPeer.CreatePeerForElement(plain).GetChildren();
                Assert.Contains(plainChildren, c => c.GetAutomationControlType() == AutomationControlType.DataItem);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) throw failure;
    }
}
