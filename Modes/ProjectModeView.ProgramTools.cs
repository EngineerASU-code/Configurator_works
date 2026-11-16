using System;
using System.Data;
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
        if ((!isProgram && !isMatrix) || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ResetProgramTools();
            return;
        }

        ObjectActionsToolbar.Visibility = System.Windows.Visibility.Collapsed;
        ObjectListPanel.Visibility = System.Windows.Visibility.Collapsed;
        ObjectDetailsPanel.Visibility = System.Windows.Visibility.Collapsed;
        ProgramToolsHost.Visibility = System.Windows.Visibility.Visible;

        string connectionString = _connection.ToConnectionString(_selectedDatabase);
        object plcValue = GetInitialProgramPlcValue();
        object classNumber = selectedClass.ClassNumber;
        if (isProgram)
        {
            ProgramToolsTabs.SelectedIndex = 0;
            ProgramManagementEditor.Configure(connectionString, plcValue, classNumber);
        }
        else
        {
            ProgramToolsTabs.SelectedIndex = 2;
            MatrixCatalogEditor.Configure(connectionString, plcValue, classNumber);
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
        if (context == null || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
        {
            ProgramSupportEditor.ClearContext();
            return;
        }

        ProgramSupportEditor.Configure(_connection.ToConnectionString(_selectedDatabase), context);
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
