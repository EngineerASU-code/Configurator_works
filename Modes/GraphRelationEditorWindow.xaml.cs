using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class GraphRelationEditorWindow : Window
{
    private readonly IReadOnlyList<GraphTableEditorItem> _tables;

    public GraphRelationDefinition Result { get; private set; }

    public GraphRelationEditorWindow(IEnumerable<GraphTableEditorItem> tables, string sourceTable, GraphRelationDefinition existing = null)
    {
        InitializeComponent();
        _tables = (tables ?? Array.Empty<GraphTableEditorItem>()).ToList();
        SourceTableCombo.ItemsSource = _tables;
        TargetTableCombo.ItemsSource = _tables;
        SourceTableCombo.SelectedItem = _tables.FirstOrDefault(x => x.FullName.Equals(existing?.Source ?? sourceTable, StringComparison.OrdinalIgnoreCase)) ?? _tables.FirstOrDefault();
        TargetTableCombo.SelectedItem = _tables.FirstOrDefault(x => x.FullName.Equals(existing?.Target ?? "", StringComparison.OrdinalIgnoreCase))
            ?? _tables.FirstOrDefault(x => !x.FullName.Equals(sourceTable, StringComparison.OrdinalIgnoreCase)) ?? _tables.FirstOrDefault();
        if (existing != null)
        {
            Title = "Изменить логическую связь";
            RuleText.Text = existing.Rule ?? "";
            SourceColumnCombo.SelectedItem = (SourceColumnCombo.ItemsSource as IEnumerable<GraphColumnEditorItem>)?.FirstOrDefault(x => x.Name.Equals(existing.SourceColumn, StringComparison.OrdinalIgnoreCase));
            TargetColumnCombo.SelectedItem = (TargetColumnCombo.ItemsSource as IEnumerable<GraphColumnEditorItem>)?.FirstOrDefault(x => x.Name.Equals(existing.TargetColumn, StringComparison.OrdinalIgnoreCase));
            SaveButton.Content = "Сохранить";
            SaveButton.SetResourceReference(StyleProperty, "BtnSecondary");
        }
    }

    private void SourceTableCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SourceColumnCombo.ItemsSource = (SourceTableCombo.SelectedItem as GraphTableEditorItem)?.Columns.Select(x => new GraphColumnEditorItem { Name = x }).ToList();
        if (SourceColumnCombo.Items.Count > 0) SourceColumnCombo.SelectedIndex = 0;
    }

    private void TargetTableCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        TargetColumnCombo.ItemsSource = (TargetTableCombo.SelectedItem as GraphTableEditorItem)?.Columns.Select(x => new GraphColumnEditorItem { Name = x }).ToList();
        if (TargetColumnCombo.Items.Count > 0) TargetColumnCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceTableCombo.SelectedItem as GraphTableEditorItem;
        var target = TargetTableCombo.SelectedItem as GraphTableEditorItem;
        var sourceColumn = (SourceColumnCombo.SelectedItem as GraphColumnEditorItem)?.Name;
        var targetColumn = (TargetColumnCombo.SelectedItem as GraphColumnEditorItem)?.Name;
        if (source == null || target == null || source.FullName.Equals(target.FullName, StringComparison.OrdinalIgnoreCase))
        {
            Configurator.Infrastructure.ModernDialog.Show("Выберите две разные таблицы.", "Связь", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        if (string.IsNullOrWhiteSpace(sourceColumn) || string.IsNullOrWhiteSpace(targetColumn))
        {
            Configurator.Infrastructure.ModernDialog.Show("Выберите поля обеих таблиц.", "Связь", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        Result = new GraphRelationDefinition
        {
            Source = source.FullName,
            Target = target.FullName,
            SourceColumn = sourceColumn,
            TargetColumn = targetColumn,
            Rule = string.IsNullOrWhiteSpace(RuleText.Text) ? "Manual logical link" : RuleText.Text.Trim()
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class GraphColumnEditorItem
    {
        public string Name { get; init; } = "";
        public override string ToString() => Name;
    }

    public sealed class GraphTableEditorItem
    {
        public string FullName { get; init; } = "";
        public string Name { get; init; } = "";
        public List<string> Columns { get; init; } = new();
    }
}
