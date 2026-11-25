#nullable enable
using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

[Collection("WPF dialogs")]
public sealed class ProgramContextDeletionInteractionTests(WpfDialogHost host)
{
    [Fact]
    public Task SequenceDeleteButtonAndKeyBothOpenReplacementPicker() => host.RunAsync(async () =>
    {
        var authorization = typeof(UserAuthorizationService);
        var store = authorization.GetField("_document", BindingFlags.Static | BindingFlags.NonPublic)!;
        var current = authorization.GetProperty(nameof(UserAuthorizationService.CurrentUser))!;
        var oldStore = store.GetValue(null);
        var oldUser = current.GetValue(null);
        var user = new UserAccount { UserName = "delete-test", RoleName = "delete-test" };
        var document = new UserStoreDocument { Users = { user },
            Roles = { new RoleDefinition { Name = user.RoleName, Permissions = { nameof(Permission.DeleteData), nameof(Permission.EditData) } } } };
        store.SetValue(null, document);
        current.SetValue(null, user);
        var view = new ProgramManagementControl();
        var window = new Window { Content = view, ShowInTaskbar = false, Width = 1000, Height = 750 };
        try
        {
            var project = new ConfiguratorProject();
            const string program = ProgramManagementRepository.ProgramTable;
            const string sequence = ProgramManagementRepository.SequenceTable;
            const string parameter = ProgramManagementRepository.ParameterTable;
            var scope = new[] { "PLC", "PLC_Class_Prog_Number", "Prog_Record" };
            AddSchema(program, "PLC", "PLC_Class_Number", "Record");
            AddSchema(sequence, scope.Append("Record").ToArray());
            AddSchema(parameter, scope.Concat(new[] { "Seq_Record", "Record", "Step_Number", "Type" }).ToArray());
            AddSchema(ProgramManagementRepository.TransitionTable, scope.Concat(new[] { "Seq_Record", "SeqStep_Number", "PLC_Class_Step_Number", "Step_Record" }).ToArray());
            AddSchema(ProgramManagementRepository.StepTable, "PLC", "PLC_Class_Number", "Record");
            void AddSchema(string table, params string[] names)
            {
                var schema = new ProjectTableSchema { TableName = table, HasStableKey = true };
                schema.KeyColumns.AddRange(names);
                foreach (var name in names)
                    schema.Columns.Add(new ProjectColumnSchema { Name = name, DataType = "int" });
                project.TableSchemas.Add(schema);
            }
            project.LocalRecords.Add(new ProjectRecord { TableName = program, Values = new JsonObject
                { ["PLC"] = 1, ["PLC_Class_Number"] = 10, ["Record"] = 1 } });
            foreach (int record in new[] { 1, 2 })
                project.LocalRecords.Add(new ProjectRecord { TableName = sequence, Values = new JsonObject
                    { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10, ["Prog_Record"] = 1, ["Record"] = record } });
            project.LocalRecords.Add(new ProjectRecord { TableName = parameter, Values = new JsonObject
                { ["PLC"] = 1, ["PLC_Class_Prog_Number"] = 10, ["Prog_Record"] = 1, ["Seq_Record"] = 1, ["Record"] = 1, ["Step_Number"] = 0, ["Type"] = 0 } });
            var source = new OfflineProjectDataSource(project);

            window.Show();
            Assert.True(await view.OpenProgramAsync(null!, source, 1, 10, 1));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var grid = view.SequencesGrid;
            Assert.Equal(1, ((DataRowView)grid.SelectedItem)["Record"]);
            var delete = FindVisibleDelete(view);
            Assert.NotNull(delete);
            Assert.True(delete.IsEnabled);

            await AssertPicker(() => delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.True(grid.IsEnabled, "Grid stayed disabled after cancel.");
            Assert.True(view.DeleteSequenceButton.IsEnabled, "Delete action stayed disabled after cancel.");
            await AssertPicker(() => grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
            Assert.Equal(2, source.GetRecords(sequence).Count);
            Assert.Equal(1, Assert.Single(source.GetRecords(parameter)).Values["Seq_Record"]!.GetValue<int>());

            async Task AssertPicker(Action trigger)
            {
                bool pickerOpened = false;
                object? candidate = null;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    var dialog = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(item => item.Owner == window && item.IsVisible);
                    if (dialog == null) return;
                    pickerOpened = dialog.Title == "Удаление последовательности" &&
                        dialog.Content is StackPanel { Children: var children } && children.OfType<ListBox>().FirstOrDefault() is { } options &&
                        options.Items.Count == 1;
                    if (pickerOpened)
                    {
                        var list = ((StackPanel)dialog.Content).Children.OfType<ListBox>().Single();
                        list.SelectedIndex = 0;
                        candidate = list.SelectedValue;
                    }
                    dialog.Close();
                };
                timer.Start();
                try
                {
                    trigger();
                    for (int attempt = 0; attempt < 100 && !pickerOpened; attempt++)
                        await Task.Delay(10);
                }
                finally { timer.Stop(); }
                Assert.True(pickerOpened);
                Assert.Equal(2, candidate);
            }
        }
        finally
        {
            window.Close();
            current.SetValue(null, oldUser);
            store.SetValue(null, oldStore);
        }
    });

    private static Button? FindVisibleDelete(DependencyObject root)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is Button { IsVisible: true, Content: "Удалить" } button) return button;
            var nested = FindVisibleDelete(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
