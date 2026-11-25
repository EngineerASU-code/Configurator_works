using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Configurator.Core.Architecture;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class DeleteInteractionTests(WpfDialogHost host)
{
    [Fact]
    public Task EnterConfirmsDeletionPrompt() => host.RunAsync(() =>
    {
        const string title = "Проверка подтверждения удаления";
        System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<Window>().Single(w => w.Title == title);
            try { dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        var result = Configurator.Infrastructure.ModernDialog.Show("Удалить подключение?", title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        Assert.Equal(MessageBoxResult.Yes, result);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StagedDeletionSelectsSameRootAcrossDifferentChildTables() => host.RunAsync(() =>
    {
        var window = new DependencyReplacementWindow(new[]
        {
            Option("dbo.Matrix_Commands", "Record"),
            Option("dbo.Prog_Queue", "Matrix_Record")
        }, requireExistingCandidates: true, stageDeletion: true);
        try
        {
            var selectors = window.ItemsHost.Children.Cast<Border>()
                .Select(border => ((Grid)border.Child).Children.OfType<ComboBox>().Single()).ToArray();
            Assert.Equal(2, selectors.Length);
            selectors[0].SelectedIndex = 1;
            Assert.Equal(2, ((ReplacementCandidate)selectors[1].SelectedItem).Values.Single());
            selectors[1].SelectedIndex = 0;
            Assert.Equal(1, ((ReplacementCandidate)selectors[0].SelectedItem).Values.Single());
        }
        finally { window.Close(); }
        return Task.CompletedTask;

        static DependencyReplacementWindow.DependencyOption Option(string table, string column)
        {
            var relation = new Configurator.Core.Architecture.DatabaseRelation { SourceTable = table };
            relation.SourceColumns.Add(column);
            relation.TargetColumns.Add("Record");
            return new DependencyReplacementWindow.DependencyOption
            {
                Hit = new Configurator.Core.Architecture.DependencyHit { SourceTable = table, Relation = relation },
                CurrentValue = 0,
                Candidates = new[] { Candidate(1), Candidate(2) }
            };
        }

        static ReplacementCandidate Candidate(int record) => new()
        {
            Values = new object[] { record },
            TargetValues = new Dictionary<string, object> { ["Record"] = record, ["PLC"] = 1 },
            Display = record.ToString()
        };
    });

    [Fact]
    public Task ReplacementShowsOnlyNamesAndKeepsSameNameCandidatesSelectable() => host.RunAsync(() =>
    {
        var relation = new DatabaseRelation();
        relation.SourceColumns.Add("ParentId");
        relation.TargetColumns.Add("Id");
        var option = new DependencyReplacementWindow.DependencyOption
        {
            Hit = new DependencyHit
            {
                SourceTable = "dbo.Children", TargetTable = "dbo.Parents", Relation = relation,
                SourceKey = new Dictionary<string, object> { ["Id"] = 10 },
                SourceValues = new Dictionary<string, object> { ["Name"] = "Насос", ["Type_Class_PLC"] = "Reglers" },
                TargetName = "Старый цех", TargetKey = new Dictionary<string, object> { ["Id"] = 1 }
            },
            Candidates =
            [
                new() { Display = "Цех", Values = [2] },
                new() { Display = "Цех", Values = [3] },
                new() { Values = [4] },
                new() { Display = "Офис", Values = [5] }
            ]
        };
        var window = new DependencyReplacementWindow([option], requireExistingCandidates: true);
        try
        {
            var grid = (Grid)((Border)window.ItemsHost.Children[0]).Child;
            var details = grid.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>();
            Assert.Contains(details, text => text.Text == "Reglers Насос ссылается на:");
            Assert.Contains(details, text => text.Text == "Parents Старый цех");

            var combo = grid.Children.OfType<ComboBox>().Single();
            Assert.Equal("Заменить на", grid.Children.OfType<Label>().Single(label => label.Target == combo).Content);
            var expected = new[] { "Цех", "Цех", "Id = 4", "Офис" };
            for (var i = 0; i < expected.Length; i++)
            {
                var presenter = new ContentPresenter { Content = combo.Items[i], ContentTemplate = combo.ItemTemplate };
                presenter.ApplyTemplate();
                var item = Assert.IsType<TextBlock>(System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0));
                Assert.Equal(expected[i], item.Text);
                Assert.Equal(expected[i], item.ToolTip);
                combo.SelectedIndex = i;
                Assert.Same(option.Candidates[i], combo.SelectedItem);
            }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task DisconnectedDatabaseCanBeSelectedAndRegainsExpansionAfterRecovery() => host.RunAsync(() =>
    {
        var tree = new TreeView();
        var node = new TreeViewItem();
        tree.Items.Add(node);
        MainWindow.SetDatabaseNodeAvailability(node, "Test", true);
        node.IsExpanded = true;

        MainWindow.SetDatabaseNodeAvailability(node, "Test", false);
        node.IsSelected = true;
        Assert.Same(node, tree.SelectedItem);
        Assert.True(node.IsEnabled);
        Assert.False(node.IsExpanded);
        Assert.False(node.HasItems);

        MainWindow.SetDatabaseNodeAvailability(node, "Test", true);
        Assert.True(node.HasItems);
        Assert.False(node.IsExpanded);
        Assert.Same(node, tree.SelectedItem);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Mode3DeleteUsesEnabledActionAndLeavesTextEditingAlone() => host.RunAsync(() =>
    {
        var editor = new TextBox();
        var list = new ListBox();
        var action = new Button();
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(editor);
        var window = new Window { Content = panel, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            int calls = 0;
            action.Click += (_, _) => calls++;
            panel.PreviewKeyDown += (_, e) => Mode3DeleteShortcut.Handle(e, action);
            void Delete(UIElement target) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Delete(editor);
            Assert.Equal(0, calls);
            Delete(list);
            Assert.Equal(1, calls);
            action.IsEnabled = false;
            Delete(list);
            Assert.Equal(1, calls);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
