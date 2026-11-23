using System;
using System.Data;
using System.Linq;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class ProjectModeView
{
    private void InitializeProgramTools()
    {
        ProgramManagementEditor.ProgramSaved += row =>
        {
            if (!IsProgramCardActive || ObjectList.SelectedItem is not ObjectItem item ||
                item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count) return;
            var destination = _objects.Rows[item.RowIndex];
            foreach (DataColumn column in row.Table.Columns)
                if (destination.Table.Columns.Contains(column.ColumnName) &&
                    !destination.Table.Columns[column.ColumnName].ReadOnly)
                    destination[column.ColumnName] = row[column];
            destination.AcceptChanges();
            ApplyObjectFilters();
            ObjectList.SelectedItem = ObjectList.Items.OfType<ObjectItem>().FirstOrDefault(candidate => candidate.RowIndex == item.RowIndex);
        };
        ProgramManagementEditor.ContextChanged += ProgramContextChanged;
        ProgramManagementEditor.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        MatrixCatalogEditor.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        ProgramManagementEditor.BeforeContextChangeAsync = ProgramManagementEditor.PrepareSupportForContextChangeAsync;
    }

    private void ConfigureProgramToolsForClass(ClassDefinition selectedClass)
    {
        bool isProgram = selectedClass?.Name.Equals("Program", StringComparison.OrdinalIgnoreCase) == true;
        bool isMatrix = selectedClass?.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase) == true;
        bool offline = _offlineProject != null && _offlineSource != null;
        if ((!isProgram && !isMatrix) || (!offline && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))))
        {
            ResetProgramTools();
            return;
        }

        var visibility = isProgram ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ObjectActionsToolbar.Visibility = ObjectListPanel.Visibility = System.Windows.Visibility.Visible;
        ProgramToolsTabs.Visibility = visibility;
        ObjectDetailsPanel.Visibility = System.Windows.Visibility.Collapsed;
        ProgramToolsHost.Visibility = System.Windows.Visibility.Visible;
        System.Windows.Controls.Grid.SetColumn(ProgramToolsHost, 4);
        System.Windows.Controls.Grid.SetColumnSpan(ProgramToolsHost, 1);
        MatrixCatalogEditor.Visibility = isMatrix ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (isProgram) ShowSelectedProgram();
        else ShowSelectedMatrix();
    }

    private async void ShowSelectedMatrix()
    {
        if (ProgramToolsHost.Visibility != System.Windows.Visibility.Visible ||
            _selectedClass?.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase) != true) return;
        if (ObjectList.SelectedItem is not ObjectItem item || item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count)
        {
            MatrixCatalogEditor.Visibility = System.Windows.Visibility.Collapsed;
            ObjectDetailsPanel.Visibility = System.Windows.Visibility.Visible;
            return;
        }
        var row = _objects.Rows[item.RowIndex];
        if (!row.Table.Columns.Contains("Record") || !row.Table.Columns.Contains("PLC")) return;
        ObjectList.IsEnabled = false;
        try
        {
            if (!await MatrixCatalogEditor.PrepareForContextChangeAsync())
            {
                if (_matrixSelection != null && ObjectList.Items.Contains(_matrixSelection))
                    ObjectList.SelectedItem = _matrixSelection;
                return;
            }
            if (!ReferenceEquals(ObjectList.SelectedItem, item)) return;
            _matrixSelection = item;
            ObjectDetailsPanel.Visibility = System.Windows.Visibility.Collapsed;
            MatrixCatalogEditor.Visibility = System.Windows.Visibility.Visible;
            var deviceClasses = (_architecture?.Classes ?? Array.Empty<ClassDefinition>())
                .Where(cls => cls != null && cls.ClassNumber > 0 && !string.IsNullOrWhiteSpace(cls.PrimaryStorage?.TableName))
                .Select(cls => new MatrixDeviceClassOption(cls.ClassNumber, cls.Name, cls.PrimaryStorage.TableName))
                .GroupBy(option => option.Number)
                .Select(group => group.First())
                .ToArray();
            string? connection = _offlineSource == null ? _connection?.ToConnectionString(_selectedDatabase) : null;
            var cls = row.Table.Columns.Contains("PLC_Class_Number") ? row["PLC_Class_Number"] : _selectedClass.ClassNumber;
            await MatrixCatalogEditor.OpenMatrixAsync(connection, _offlineSource, row["PLC"], cls, row["Record"], item.Name, deviceClasses);
        }
        finally { ObjectList.IsEnabled = true; }
    }

    private ObjectItem? _matrixSelection;

    private bool IsProgramCardActive => ProgramToolsHost.Visibility == System.Windows.Visibility.Visible &&
        ProgramToolsTabs.Visibility == System.Windows.Visibility.Visible &&
        _selectedClass?.Name.Equals("Program", StringComparison.OrdinalIgnoreCase) == true;

    private async void ShowSelectedProgram()
    {
        if (!IsProgramCardActive) return;
        if (ObjectList.SelectedItem is not ObjectItem item || item.RowIndex < 0 || item.RowIndex >= _objects.Rows.Count)
        {
            ProgramCardTitle.Text = "Выберите программу";
            ProgramManagementEditor.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        var row = _objects.Rows[item.RowIndex];
        if (!row.Table.Columns.Contains("PLC") || !row.Table.Columns.Contains("Record")) return;
        var cls = row.Table.Columns.Contains("PLC_Class_Number") ? row["PLC_Class_Number"] : _selectedClass.ClassNumber;
        var connection = _offlineSource == null ? _connection?.ToConnectionString(_selectedDatabase) : null;
        bool opened;
        ObjectList.IsEnabled = false;
        try { opened = await ProgramManagementEditor.OpenProgramAsync(connection, _offlineSource, row["PLC"], cls, row["Record"]); }
        finally { ObjectList.IsEnabled = true; }
        if (!IsProgramCardActive) return;
        if (!opened)
        {
            var context = ProgramManagementEditor.CurrentContext;
            if (context != null)
                ObjectList.SelectedItem = ObjectList.Items.OfType<ObjectItem>().FirstOrDefault(candidate =>
                    candidate.RowIndex >= 0 && candidate.RowIndex < _objects.Rows.Count &&
                    Equals(_objects.Rows[candidate.RowIndex]["Record"], context.RecordValue) &&
                    Equals(_objects.Rows[candidate.RowIndex]["PLC"], context.PlcValue) &&
                    (!_objects.Columns.Contains("PLC_Class_Number") || Equals(_objects.Rows[candidate.RowIndex]["PLC_Class_Number"], context.ClassNumberValue)));
            return;
        }
        if (!ReferenceEquals(ObjectList.SelectedItem, item)) return;
        ProgramCardTitle.Text = item.Name;
        ProgramManagementEditor.Visibility = System.Windows.Visibility.Visible;
    }

    private void ProgramContextChanged(ProgramContext context)
        => ProgramManagementEditor.UpdateSupportContext(context);

    private void ResetProgramTools()
    {
        if (ObjectActionsToolbar == null) return;
        ObjectActionsToolbar.Visibility = System.Windows.Visibility.Visible;
        ObjectListPanel.Visibility = System.Windows.Visibility.Visible;
        ObjectDetailsPanel.Visibility = System.Windows.Visibility.Visible;
        ProgramToolsHost.Visibility = System.Windows.Visibility.Collapsed;
    }
}
