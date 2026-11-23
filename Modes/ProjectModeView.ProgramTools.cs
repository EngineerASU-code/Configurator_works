using System;
using System.Data;
using System.Linq;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class ProjectModeView
{
    private void InitializeProgramTools()
    {
        ProgramManagementEditor.ContextChanged += ProgramContextChanged;
        ProgramManagementEditor.ChangeJournalRequested += entry => ChangeJournalRequested?.Invoke(entry);
        ProgramManagementEditor.BeforeContextChangeAsync = ProgramSupportEditor.PrepareForContextChangeAsync;
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

        ObjectActionsToolbar.Visibility = System.Windows.Visibility.Collapsed;
        ObjectListPanel.Visibility = System.Windows.Visibility.Collapsed;
        ObjectDetailsPanel.Visibility = System.Windows.Visibility.Collapsed;
        ProgramToolsHost.Visibility = System.Windows.Visibility.Visible;

        string? connectionString = offline ? null : _connection.ToConnectionString(_selectedDatabase);
        object plcValue = GetInitialProgramPlcValue();
        object classNumber = selectedClass.ClassNumber;
        if (isProgram)
        {
            ProgramToolsTabs.Visibility = System.Windows.Visibility.Visible;
            MatrixCatalogEditor.Visibility = System.Windows.Visibility.Collapsed;
            ProgramToolsTabs.SelectedIndex = 0;
            if (offline) ProgramManagementEditor.Configure(_offlineSource, plcValue, classNumber);
            else ProgramManagementEditor.Configure(connectionString!, plcValue, classNumber);
        }
        else
        {
            ProgramToolsTabs.Visibility = System.Windows.Visibility.Collapsed;
            MatrixCatalogEditor.Visibility = System.Windows.Visibility.Visible;
            var deviceClasses = (_architecture?.Classes ?? Array.Empty<ClassDefinition>())
                .Where(cls => cls != null && cls.ClassNumber > 0 && !string.IsNullOrWhiteSpace(cls.PrimaryStorage?.TableName))
                .Select(cls => new MatrixDeviceClassOption(cls.ClassNumber, cls.Name, cls.PrimaryStorage.TableName))
                .GroupBy(option => option.Number)
                .Select(group => group.First())
                .ToArray();
            if (offline) MatrixCatalogEditor.Configure(_offlineSource, plcValue, classNumber, deviceClasses);
            else MatrixCatalogEditor.Configure(connectionString!, plcValue, classNumber, deviceClasses);
        }
    }

    private object GetInitialProgramPlcValue()
    {
        if (_objects?.Rows.Count > 0 && _objects.Columns.Contains("PLC"))
        {
            var value = _objects.Rows[0]["PLC"];
            if (value != null && value != DBNull.Value) return value;
        }
        return 1;
    }

    private void ProgramContextChanged(ProgramContext context)
    {
        bool offline = _offlineProject != null && _offlineSource != null;
        if (context == null || (!offline && (_connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))))
        {
            ProgramSupportEditor.ClearContext();
            return;
        }

        if (offline) ProgramSupportEditor.Configure(_offlineSource, context);
        else ProgramSupportEditor.Configure(_connection!.ToConnectionString(_selectedDatabase), context);
    }

    private void ResetProgramTools()
    {
        if (ObjectActionsToolbar == null) return;
        ObjectActionsToolbar.Visibility = System.Windows.Visibility.Visible;
        ObjectListPanel.Visibility = System.Windows.Visibility.Visible;
        ObjectDetailsPanel.Visibility = System.Windows.Visibility.Visible;
        ProgramToolsHost.Visibility = System.Windows.Visibility.Collapsed;
    }
}
