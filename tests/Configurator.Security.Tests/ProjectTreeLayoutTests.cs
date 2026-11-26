using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class ProjectTreeLayoutTests(WpfDialogHost host)
{
    [Fact]
    public Task ExpandingScrollableProjectPreservesCardWidth() => host.RunAsync(() =>
    {
        var panel = new LeftPanel();
        var tree = (TreeView)panel.FindName("ServerTree");
        var project = new TreeViewItem { Header = "Test project" };
        for (int i = 0; i < 80; i++) project.Items.Add("dbo.Table_" + i);
        tree.Items.Add(project);
        var window = new Window { Width = 320, Height = 380, Content = panel, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var card = (Border)project.Template.FindName("ItemBorder", project);
            void ClickCard() => card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            double width = card.ActualWidth;
            Assert.True(width > 0);
            Assert.Null(project.Template.FindName("Expander", project));
            ClickCard();
            Assert.True(project.IsExpanded);
            window.UpdateLayout();
            Assert.Equal(width, card.ActualWidth);
            ClickCard();
            Assert.False(project.IsExpanded);
            window.UpdateLayout();
            Assert.Equal(width, card.ActualWidth);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
