using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class DatabaseGraphView
{
    private void StartCompactGraphSync()
    {
        var previous = _selectedTable;
        var items = _tables.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        GraphTableList.ItemsSource = items;

        GraphTable restored = null;
        if (!string.IsNullOrWhiteSpace(previous))
            restored = items.FirstOrDefault(x => x.FullName.Equals(previous, StringComparison.OrdinalIgnoreCase));

        GraphTableList.SelectedItem = restored;
        _selectedTable = restored?.FullName;
        RenderGraph();
    }

    private void GraphTableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GraphTableList.SelectedItem is not GraphTable table) return;
        _selectedTable = table.FullName;
        // Keep the user's current viewport and manually arranged positions when
        // switching the table in the left list.
        RenderGraph();
    }

    private void UpdateGraphRelationButtons()
    {
        if (BtnAddGraphRelation != null) BtnAddGraphRelation.IsEnabled = false;
        if (BtnEditGraphRelation != null) BtnEditGraphRelation.IsEnabled = false;
        if (BtnRemoveGraphRelation != null) BtnRemoveGraphRelation.IsEnabled = false;
    }
}
