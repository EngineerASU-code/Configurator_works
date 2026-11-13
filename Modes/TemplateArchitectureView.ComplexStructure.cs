using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class TemplateArchitectureView
{
    private async Task LoadComplexStructureAsync(ClassDefinition selected)
    {
        ComplexStructureHost.Children.Clear();
        ComplexStructureHost.Visibility = Visibility.Collapsed;

        if (!IsComplexClass(selected) || _connection == null || string.IsNullOrWhiteSpace(_selectedDatabase))
            return;

        try
        {
            var db = new DatabaseService(_connection.ToConnectionString(_selectedDatabase));
            var tableList = await db.GetTableListAsync();
            var names = tableList.Rows.Cast<DataRow>()
                .Select(r => $"{Convert.ToString(r["TABLE_SCHEMA"])}.{Convert.ToString(r["TABLE_NAME"])}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            string root = ResolveComplexRoot(selected, names);
            if (string.IsNullOrWhiteSpace(root))
                return;

            var prefix = selected.Name.Equals("Program", StringComparison.OrdinalIgnoreCase) ? "Prog_" : "Matrix_";
            var children = names
                .Where(x => x.Contains('.') && x[(x.IndexOf('.') + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The root table is also a real table in the architecture. Keep the
            // normal Object/Config groups first, then show the root table itself,
            // followed by its actual child tables.
            var ordered = new List<(string Table, string Role)>();
            ordered.Add((root, "Основная таблица"));
            ordered.AddRange(children
                .Where(x => !x.Equals(root, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x, "Связанная таблица")));

            var existing = selected.ComplexTables.ToDictionary(x => x.TableName, StringComparer.OrdinalIgnoreCase);
            selected.ComplexTables.Clear();

            foreach (var item in ordered)
            {
                var definition = existing.TryGetValue(item.Table, out var saved)
                    ? saved
                    : new ComplexTableDefinition { TableName = item.Table, Role = item.Role };

                var columns = await db.GetTableColumnsAsync(item.Table);
                var oldFields = definition.Fields.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
                var fields = new List<FieldDefinition>();
                foreach (var column in columns)
                {
                    if (oldFields.TryGetValue(column.Name, out var old))
                    {
                        old.DataType = column.DataType;
                        old.Size = column.MaxLength;
                        old.Nullable = column.IsNullable;
                        old.Group = FieldGroupCatalog.Resolve(selected.Name, column.Name);
                        fields.Add(old);
                    }
                    else
                    {
                        fields.Add(new FieldDefinition
                        {
                            Name = column.Name,
                            DataType = column.DataType,
                            Size = column.MaxLength,
                            Nullable = column.IsNullable,
                            Group = FieldGroupCatalog.Resolve(selected.Name, column.Name)
                        });
                    }
                }

                definition.Fields = fields;
                definition.Description = item.Role;
                selected.ComplexTables.Add(definition);
            }

            RenderComplexStructure(selected);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Сложная структура не загружена: {ex.Message}";
        }
    }

    private static bool IsComplexClass(ClassDefinition definition)
        => definition != null && (definition.Name.Equals("Program", StringComparison.OrdinalIgnoreCase)
                                   || definition.Name.Equals("Matrix", StringComparison.OrdinalIgnoreCase));

    private static string ResolveComplexRoot(ClassDefinition selected, IReadOnlyList<string> names)
    {
        var mapped = selected.PrimaryStorage?.TableName;
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            var exact = names.FirstOrDefault(x => x.Equals(mapped, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var candidates = selected.Name.Equals("Program", StringComparison.OrdinalIgnoreCase)
            ? new[] { "dbo.Prog", "dbo.Programs", "dbo.Program" }
            : new[] { "dbo.Matrix_List", "dbo.Matrix", "dbo.Matrices" };
        return candidates.Select(c => names.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(x => x != null);
    }

    private void RenderComplexStructure(ClassDefinition selected)
    {
        ComplexStructureHost.Children.Clear();
        if (!IsComplexClass(selected) || selected.ComplexTables.Count == 0)
        {
            ComplexStructureHost.Visibility = Visibility.Collapsed;
            return;
        }

        ComplexStructureHost.Visibility = Visibility.Visible;
        foreach (var table in selected.ComplexTables)
        {
            var expander = new Expander
            {
                Header = $"{table.Role} · {table.TableName}",
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 7, 10, 8)
            };
            expander.SetResourceReference(Control.BackgroundProperty, "BrushBase");
            expander.SetResourceReference(Control.ForegroundProperty, "BrushText");

            var stack = new StackPanel();
            var caption = new TextBlock
            {
                Text = $"Фактическая структура · полей: {table.Fields.Count}",
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            stack.Children.Add(caption);

            var header = CreateFieldHeader();
            stack.Children.Add(header);
            var list = new ListBox
            {
                ItemsSource = table.Fields,
                ItemTemplate = (DataTemplate)FindResource("FieldRowTemplate"),
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                SelectionMode = SelectionMode.Single,
                Tag = table.TableName
            };
            list.SelectionChanged += ComplexFieldList_SelectionChanged;
            stack.Children.Add(list);
            expander.Content = stack;
            ComplexStructureHost.Children.Add(expander);
        }
    }

    private void ComplexFieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is FieldDefinition field)
            _selectedField = field;
    }
}
