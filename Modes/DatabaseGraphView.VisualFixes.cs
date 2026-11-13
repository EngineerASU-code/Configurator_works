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

    private void AddGraphRelation_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireRelationEditPermission()) return;
        AddManualGraphRelation();
    }

    private bool RequireRelationEditPermission()
        => UserAuthorizationService.IsAdministrator || UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Require(Permission.ManageRelations);

    private void EditGraphRelation_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireRelationEditPermission()) return;
        EditSelectedGraphRelation();
    }

    private void RemoveGraphRelation_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireRelationEditPermission()) return;
        RemoveSelectedGraphRelation();
    }

    private void UpdateGraphRelationButtons()
    {
        bool canEditRelations = UserAuthorizationService.IsAdministrator || UserAuthorizationService.Has(Permission.Mode2Manage) || UserAuthorizationService.Has(Permission.ManageRelations);
        bool editableLogical = canEditRelations && _selectedGraphRelation != null && _selectedGraphRelation.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase);
        if (BtnAddGraphRelation != null) BtnAddGraphRelation.IsEnabled = canEditRelations;
        if (BtnRemoveGraphRelation != null) BtnRemoveGraphRelation.IsEnabled = editableLogical;
        if (BtnEditGraphRelation != null) BtnEditGraphRelation.IsEnabled = editableLogical;
    }

    private void EditSelectedGraphRelation()
    {
        if (_selectedGraphRelation == null || !_selectedGraphRelation.Kind.Equals("LOGICAL", StringComparison.OrdinalIgnoreCase)) return;
        var relation = _selectedGraphRelation;
        var items = _tables.Select(t => new GraphRelationEditorWindow.GraphTableEditorItem
        {
            FullName = t.FullName, Name = t.Name, Columns = t.Columns.ToList()
        }).ToList();
        var existing = new GraphRelationDefinition
        {
            Source = relation.Source, Target = relation.Target,
            SourceColumn = relation.SourceColumns.FirstOrDefault() ?? "",
            TargetColumn = relation.TargetColumns.FirstOrDefault() ?? "", Rule = relation.Rule ?? ""
        };
        var dialog = new GraphRelationEditorWindow(items, relation.Source, existing) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;

        var updated = dialog.Result;
        bool duplicate = _relations.Any(r => !ReferenceEquals(r, relation) &&
            r.Source.Equals(updated.Source, StringComparison.OrdinalIgnoreCase) && r.Target.Equals(updated.Target, StringComparison.OrdinalIgnoreCase) &&
            r.SourceColumns.SequenceEqual(new[] { updated.SourceColumn }, StringComparer.OrdinalIgnoreCase) &&
            r.TargetColumns.SequenceEqual(new[] { updated.TargetColumn }, StringComparer.OrdinalIgnoreCase));
        if (duplicate)
        {
            Configurator.Infrastructure.ModernDialog.Show("Такая связь уже существует. Изменения не сохранены.", "Связи и правила", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _graphRelationStore ??= new GraphRelationStore(_connection.Server, _database);
        _graphRelationDocument.ManualRelations ??= new List<GraphRelationDefinition>();
        _graphRelationDocument.SuppressedAutomaticRelations ??= new List<string>();
        if (relation.IsManual)
        {
            _graphRelationDocument.ManualRelations.RemoveAll(x =>
                x.Source.Equals(relation.Source, StringComparison.OrdinalIgnoreCase) &&
                x.Target.Equals(relation.Target, StringComparison.OrdinalIgnoreCase) &&
                x.SourceColumn.Equals(relation.SourceColumns.FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase) &&
                x.TargetColumn.Equals(relation.TargetColumns.FirstOrDefault() ?? "", StringComparison.OrdinalIgnoreCase));
        }
        else if (!_graphRelationDocument.SuppressedAutomaticRelations.Contains(relation.Id, StringComparer.OrdinalIgnoreCase))
        {
            // Editing an inferred logical relation creates a manual replacement and suppresses the inferred version.
            _graphRelationDocument.SuppressedAutomaticRelations.Add(relation.Id);
        }
        _graphRelationDocument.ManualRelations.Add(updated);
        _graphRelationStore.Save(_graphRelationDocument);
        LoadConfiguredGraphRelations();
        RenderGraph();
        RefreshRulesList();
        _selectedGraphRelation = _relations.FirstOrDefault(r => r.IsManual && r.Source.Equals(updated.Source, StringComparison.OrdinalIgnoreCase) &&
            r.Target.Equals(updated.Target, StringComparison.OrdinalIgnoreCase) && r.SourceColumns.SequenceEqual(new[] { updated.SourceColumn }, StringComparer.OrdinalIgnoreCase) &&
            r.TargetColumns.SequenceEqual(new[] { updated.TargetColumn }, StringComparer.OrdinalIgnoreCase));
        RulesList.SelectedItem = _selectedGraphRelation;
        UpdateGraphRelationButtons();
        ActionLogged?.Invoke("Изменена логическая связь", updated.Source, $"{updated.Source}.{updated.SourceColumn} → {updated.Target}.{updated.TargetColumn}; правило={updated.Rule}");
    }
}
