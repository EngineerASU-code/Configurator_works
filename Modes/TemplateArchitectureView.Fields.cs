using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class TemplateArchitectureView
{
private async Task LoadFieldsAsync(ClassDefinition selected)
    {
        CancelLoad();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        if (_reader == null || selected.PrimaryStorage == null)
        {
            RebuildGroups();
            return;
        }

        try
        {
            var fields = await _reader.LoadFieldsAsync(selected.PrimaryStorage.TableName, selected.Name, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_selectedClass, selected))
                return;

            if (_offlineProject == null && _connection != null)
            {
                try
                {
                    var schema = await new DatabaseArchitectureAnalyzer(_connection.ToConnectionString(_selectedDatabase)).AnalyzeAsync(ct);
                    ApplyInferredFieldRelations(fields, schema, selected.PrimaryStorage.TableName);
                }
                catch { }
            }

            if (selected.IsSystem)
            {
                if (selected.Name.Equals("DSV", StringComparison.OrdinalIgnoreCase) && string.Equals(selected.PrimaryStorage.TableName, "dbo.Valve2S", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var field in fields) field.Group = FieldGroupCatalog.Resolve(selected.Name, field.Name);
                    selected.Fields = fields;
                    TxtStatus.Text = $"DSV · dbo.Valve2S · SQL-столбцов: {fields.Count}";
                    RebuildGroups();
                    return;
                }
                // Для системных классов фактическая SQL-таблица является источником
                // истины по составу полей. Сохраняем только человекочитаемые
                // описания из каталога, но никогда не ограничиваем набор колонок
                // заранее заданными семью полями.
                var metadata = selected.Fields
                    .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    if (metadata.TryGetValue(field.Name, out var old))
                    {
                        field.Group = !string.IsNullOrWhiteSpace(old.Group) ? old.Group : FieldGroupCatalog.Resolve(selected.Name, field.Name);
                        field.Description = old.Description;
                        if (!string.IsNullOrWhiteSpace(old.LookupTable) && !string.IsNullOrWhiteSpace(old.LookupKeyField))
                        {
                            field.LookupTable = old.LookupTable;
                            field.LookupKeyField = old.LookupKeyField;
                            field.LookupDisplayField = old.LookupDisplayField;
                        }
                    }
                    else
                        field.Group = FieldGroupCatalog.Resolve(selected.Name, field.Name);
                }
                selected.Fields = fields;
                TxtStatus.Text = $"{selected.Name} · SQL-полей: {fields.Count}";
            }
            else if (fields.Count > 0)
            {
                selected.Fields = MergeFields(selected.Fields, fields, selected.Name);
            }

            RebuildGroups();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RebuildGroups();
            TxtStatus.Text = $"Структура SQL не загружена: {Configurator.Infrastructure.UserErrorMessage.Format(ex)}";
        }
    }

private static void ApplyInferredFieldRelations(List<FieldDefinition> fields, DatabaseSchema schema, string sourceTable)
    {
        if (fields == null || schema == null || string.IsNullOrWhiteSpace(sourceTable)) return;
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.LookupTable) && !string.IsNullOrWhiteSpace(field.LookupKeyField)) continue;
            // Some legacy project databases use logical type links without SQL FK constraints.
            // Restore these known links from the domain catalog (for example Valve.Type -> ValveType.Type).
            var catalogLink = SystemLookupCatalog.Find(sourceTable, field.Name);
            if (catalogLink != null && schema.FindTable(catalogLink.LookupTable) != null)
            {
                field.LookupTable = catalogLink.LookupTable;
                field.LookupKeyField = catalogLink.LookupKeyField;
                var lookupTable = schema.FindTable(catalogLink.LookupTable);
                field.LookupDisplayField = lookupTable?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase))
                    ?? lookupTable?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    ?? lookupTable?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase))
                    ?? catalogLink.LookupKeyField;
                continue;
            }
            var relation = schema.Relations.FirstOrDefault(r =>
                r.SourceTable.Equals(sourceTable, StringComparison.OrdinalIgnoreCase) &&
                r.SourceColumns.Count == 1 &&
                r.SourceColumns[0].Equals(field.Name, StringComparison.OrdinalIgnoreCase));
            if (relation == null || relation.TargetColumns.Count != 1) continue;
            field.LookupTable = relation.TargetTable;
            field.LookupKeyField = relation.TargetColumns[0];
            var target = schema.FindTable(relation.TargetTable);
            field.LookupDisplayField = target?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("NameL1", StringComparison.OrdinalIgnoreCase))
                ?? target?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("Name", StringComparison.OrdinalIgnoreCase))
                ?? target?.Columns.Select(c => c.Name).FirstOrDefault(n => n.Equals("DescriptionL1", StringComparison.OrdinalIgnoreCase))
                ?? relation.TargetColumns[0];
        }
    }

    private static List<FieldDefinition> MergeFields(List<FieldDefinition> existing, List<FieldDefinition> sqlFields, string className)
    {
        var result = new List<FieldDefinition>();
        var sqlByName = sqlFields.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var old in existing)
        {
            if (sqlByName.TryGetValue(old.Name, out var actual))
            {
                actual.Group = !string.IsNullOrWhiteSpace(old.Group) ? old.Group : FieldGroupCatalog.Resolve(className, actual.Name);
                actual.Description = old.Description;
                if (!string.IsNullOrWhiteSpace(old.LookupTable) && !string.IsNullOrWhiteSpace(old.LookupKeyField))
                {
                    actual.LookupTable = old.LookupTable;
                    actual.LookupKeyField = old.LookupKeyField;
                    actual.LookupDisplayField = old.LookupDisplayField;
                }
                result.Add(actual);
                sqlByName.Remove(old.Name);
            }
        }
        foreach (var extra in sqlFields.Where(x => sqlByName.ContainsKey(x.Name)))
            result.Add(extra);
        return result;
    }

private void RebuildGroups()
    {
        foreach (var child in GroupHost.Children.OfType<Expander>())
            if (child.Header is string header) _groupExpandedStates[header] = child.IsExpanded;
        foreach (var child in ComplexStructureHost.Children.OfType<Expander>())
            if (child.Header is string header) _groupExpandedStates[header] = child.IsExpanded;

        GroupHost.Children.Clear();
        if (_selectedClass == null) return;

        foreach (var groupName in GetDisplayGroups(_selectedClass))
        {
            // Для системных классов источником истины является фактическая SQL-таблица.
            // Не берём здесь устаревший снимок из ClassArchitectureService: после загрузки
            // dbo.Valve2S / dbo.Valves / dbo.Motors и т.д. selected.Fields уже содержит
            // полный актуальный набор колонок SQL.
            var effectiveFields = _selectedClass.IsSystem
                ? _selectedClass.Fields
                : _architecture.GetEffectiveFields(_selectedClass);
            var fields = effectiveFields.Where(f => !MainWindow.HideLanguageField(f.Name))
                .Where(x => string.Equals(
                    string.IsNullOrWhiteSpace(x.Group) ? FieldGroupCatalog.Resolve(_selectedClass.Name, x.Name) : x.Group,
                    groupName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fields.Count == 0) continue;

            var expander = new Expander
            {
                Header = groupName switch
                {
                    "Object" => "Object · Основные данные",
                    "Config" => "Config · Настройки и адреса",
                    "Visual" => "Visual · Отображение",
                    "Conditions" => "Conditions · Условия и переходы",
                    _ => groupName
                },
                IsExpanded = _groupExpandedStates.TryGetValue(groupName switch
                {
                    "Object" => "Object · Основные данные",
                    "Config" => "Config · Настройки и адреса",
                    "Visual" => "Visual · Отображение",
                    "Conditions" => "Conditions · Условия и переходы",
                    _ => groupName
                }, out var expanded) && expanded,
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 7, 10, 8)
            };
            expander.SetResourceReference(Control.BackgroundProperty, "BrushBase");
            expander.SetResourceReference(Control.ForegroundProperty, "BrushText");

            var stack = new StackPanel();
            stack.Children.Add(CreateFieldHeader());
            var list = new ListBox { ItemsSource = fields, ItemTemplate = (DataTemplate)FindResource("FieldRowTemplate"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, SelectionMode = SelectionMode.Extended, Tag = groupName };
            list.SelectionChanged += FieldList_SelectionChanged;
            list.PreviewMouseDown += FieldList_PreviewMouseDown;
            stack.Children.Add(list);
            expander.Content = stack;
            GroupHost.Children.Add(expander);
        }

    }

    private static IReadOnlyList<string> GetDisplayGroups(ClassDefinition cls)
    {
        if (cls == null) return Array.Empty<string>();
        var groups = new List<string>(FieldGroupCatalog.GetGroups(cls.Name));
        // Keep legacy field groups visible, but do not expose a group-management UI.
        foreach (var fieldGroup in cls.Fields.Select(x => x.Group).Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!groups.Contains(fieldGroup, StringComparer.OrdinalIgnoreCase)) groups.Add(fieldGroup);
        return groups;
    }

    private void FieldList_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        bool keepSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!keepSelection)
        {
            foreach (var other in GroupHost.Children.OfType<Expander>())
                foreach (var otherList in FindFieldLists(other))
                    if (!ReferenceEquals(otherList, list)) otherList.SelectedItems.Clear();
            _selectedFields.Clear();
            _selectedField = null;
        }
    }

    private static IEnumerable<ListBox> FindFieldLists(DependencyObject root)
    {
        if (root == null) yield break;
        if (root is ListBox list) { yield return list; yield break; }
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (var child in FindFieldLists(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

private static Grid CreateFieldHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        foreach (var width in new[] { 180.0, 115.0, 75.0, 75.0, 75.0, 80.0, double.NaN })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        string[] labels = { "Имя", "Тип", "Размер", "Смещение", "Бит", "Nullable", "Описание" };
        for (int i = 0; i < labels.Length; i++)
        {
            var text = new TextBlock { Text = labels[i], FontSize = 10, Margin = new Thickness(4, 0, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }
}
