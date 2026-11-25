using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Configurator.Core.Architecture;

namespace Configurator;

public partial class DependencyReplacementWindow : Window
{
    private readonly List<Row> _rows = new();
    private readonly bool _requireExistingCandidates;
    public IReadOnlyList<ReferenceReplacement> Replacements { get; private set; } = Array.Empty<ReferenceReplacement>();

    public DependencyReplacementWindow(IEnumerable<DependencyOption> options, bool requireExistingCandidates = false, bool stageDeletion = false)
    {
        _requireExistingCandidates = requireExistingCandidates;
        InitializeComponent();
        if (stageDeletion)
        {
            ApplyButton.Content = "Подготовить удаление";
        }
        var list = (options ?? Array.Empty<DependencyOption>()).ToList();
        foreach (var option in list)
            AddRow(option);

        if (stageDeletion) LinkStagedSelections();
        else BuildGlobalGroups();
    }

    private void LinkStagedSelections()
    {
        bool updating = false;
        foreach (var row in _rows)
        {
            row.Combo.SelectionChanged += (_, _) =>
            {
                if (updating || row.Combo.SelectedItem is not ReplacementCandidate selected) return;
                updating = true;
                try
                {
                    foreach (var other in _rows.Where(item => item != row))
                    {
                        other.Combo.SelectedItem = other.Option.Candidates.FirstOrDefault(candidate =>
                            candidate.TargetValues.Count == selected.TargetValues.Count &&
                            candidate.TargetValues.All(pair => selected.TargetValues.TryGetValue(pair.Key, out var current)
                                && System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(pair.Value, current)));
                    }
                }
                finally { updating = false; }
            };
        }
    }

    private void AddRow(DependencyOption option)
    {
        var row = new Row(option);
        _rows.Add(row);
        var border = new Border
        {
            Background = (Brush)FindResource("BrushPanel"), BorderBrush = (Brush)FindResource("BrushBorder"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 250 });

        var details = new StackPanel { Margin = new Thickness(2, 0, 16, 0) };
        var sourceName = option.Hit.SourceName;
        if (sourceName.Length == 0) sourceName = DependencyRecordLabel.Name(option.Hit.SourceValues);
        var sourceType = option.Hit.SourceValues.TryGetValue("Type_Class_PLC", out var type) ? Convert.ToString(type) : null;
        if (string.IsNullOrWhiteSpace(sourceType)) sourceType = ShortTableName(option.Hit.SourceTable);
        var sourceLabel = string.IsNullOrWhiteSpace(sourceName) ? DependencyRecordLabel.Format("", option.Hit.SourceKey) : sourceName;
        var targetLabel = string.IsNullOrWhiteSpace(option.Hit.TargetName) ? DependencyRecordLabel.Format("", option.Hit.TargetKey) : option.Hit.TargetName;
        if (targetLabel.Length == 0) targetLabel = Convert.ToString(option.CurrentValue);
        details.Children.Add(new TextBlock
        {
            Text = $"{sourceType} {sourceLabel} ссылается на:", FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushText"), TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{ShortTableName(option.Hit.TargetTable)} {targetLabel}",
            Margin = new Thickness(0, 3, 0, 6), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("BrushText")
        });
        Grid.SetRowSpan(details, 3);
        grid.Children.Add(details);

        row.Combo = CreateCombo(option);
        var label = new Label
        {
            Content = "Заменить на", Target = row.Combo,
            Padding = new Thickness(0, 0, 0, 4), Foreground = (Brush)FindResource("BrushText")
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(row.Combo, 1); Grid.SetRow(row.Combo, 1); grid.Children.Add(row.Combo);

        row.Status = new TextBlock { Margin = new Thickness(8, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("BrushTextSecondary"), FontSize = 11 };
        Grid.SetColumn(row.Status, 1); Grid.SetRow(row.Status, 2); grid.Children.Add(row.Status);
        row.Combo.SelectionChanged += (_, _) => UpdateCandidateStatus(row);
        if (option.Candidates.Count > 0) row.Combo.SelectedIndex = 0;

        border.Child = grid;
        ItemsHost.Children.Add(border);
    }

    private ComboBox CreateCombo(DependencyOption option)
    {
        var combo = new ComboBox { ItemsSource = option.Candidates, MinWidth = 250, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Запись для замены" };
        System.Windows.Automation.AutomationProperties.SetName(combo, "Запись для замены");
        // Never combine DisplayMemberPath with ItemTemplate: the global ComboBox style
        // may already provide an item template and WPF rejects both properties together.
        combo.ItemTemplate = CreateCandidateItemTemplate(option.Hit.Relation.TargetColumns);
        return combo;
    }

    private void BuildGlobalGroups()
    {
        var groups = _rows
            .GroupBy(x => new
            {
                Table = x.Option.Hit.SourceTable,
                Columns = string.Join("|", x.Option.Hit.Relation.SourceColumns).ToLowerInvariant()
            })
            .ToList();

        foreach (var group in groups)
        {
            var rows = group.ToList();
            var columns = rows[0].Option.Hit.Relation.SourceColumns.ToList();
            if (columns.Count == 0) continue;

            var panel = new Border
            {
                Background = (Brush)FindResource("BrushPanel"),
                BorderBrush = (Brush)FindResource("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 7)
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"Одна замена для {ShortTableName(group.Key.Table)}:",
                Foreground = (Brush)FindResource("BrushTextSecondary"),
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 10, 0)
            };
            Grid.SetColumn(label, 0);
            root.Children.Add(label);

            var choices = new List<GroupParameterChoice>();
            for (var i = 0; i < Math.Min(columns.Count, 2); i++)
            {
                var column = columns[i];
                var combo = CreateParameterCombo(rows, i);
                choices.Add(new GroupParameterChoice(column, combo));

                var wrapper = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
                wrapper.Children.Add(new TextBlock
                {
                    Text = column,
                    Foreground = (Brush)FindResource("BrushTextSecondary"),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 3)
                });
                wrapper.Children.Add(combo);
                Grid.SetColumn(wrapper, Math.Min(i + 1, 2));
                root.Children.Add(wrapper);

            }

            // If there are more than two linked columns, put the remaining editors on a second line.
            // This keeps the common two-column case compact while still supporting arbitrary relations.
            if (columns.Count > 2)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var extra = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                for (var i = 2; i < columns.Count; i++)
                {
                    var choice = new GroupParameterChoice(columns[i], CreateParameterCombo(rows, i));
                    choices.Add(choice);
                    var wrapper = new StackPanel { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
                    wrapper.Children.Add(new TextBlock
                    {
                        Text = choice.Column,
                        Foreground = (Brush)FindResource("BrushTextSecondary"),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 0, 3)
                    });
                    wrapper.Children.Add(choice.Combo);
                    extra.Children.Add(wrapper);
                }
                Grid.SetRow(extra, 1);
                Grid.SetColumn(extra, 0);
                Grid.SetColumnSpan(extra, 3);
                root.Children.Add(extra);
            }

            var button = new Button
            {
                Content = "Применить группе",
                Width = 140,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("BtnSecondary"),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            button.Click += (_, _) => ApplyParameterGroup(rows, columns, choices);
            Grid.SetColumn(button, 3);
            root.Children.Add(button);

            panel.Child = root;
            GlobalGroupsHost.Children.Add(panel);
        }
    }

    private ComboBox CreateParameterCombo(IReadOnlyList<Row> rows, int columnIndex)
    {
        bool isPlc = rows[0].Option.Hit.Relation.SourceColumns[columnIndex].Equals("PLC", StringComparison.OrdinalIgnoreCase);
        var choices = rows
            .SelectMany(row => row.Option.Candidates
                .Where(x => !x.IsConflict && x.Values.Count > columnIndex)
                .Select(x => new { Value = x.Values[columnIndex],
                    Display = isPlc ? (x.PlcLabels?.GetValueOrDefault(Convert.ToString(x.Values[columnIndex], System.Globalization.CultureInfo.InvariantCulture) ?? "")) : x.Display }))
            .GroupBy(x => x.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var display = g.Select(x => x.Display).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                return new ParameterChoice(first.Value, display, isPlc);
            })
            .ToList();

        var combo = new ComboBox
        {
            ItemsSource = choices,
            Height = 32,
            MinWidth = 165,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Новое значение"
        };
        combo.ItemTemplate = CreateParameterItemTemplate();
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private void ApplyParameterGroup(IReadOnlyList<Row> rows, IReadOnlyList<string> columns, IReadOnlyList<GroupParameterChoice> choices)
    {
        var selected = new object[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            if (choices[i].Combo.SelectedItem is not ParameterChoice value)
            {
                Configurator.Infrastructure.ModernDialog.Show($"Для параметра {columns[i]} выберите новое значение.", "Замена ссылок", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            selected[i] = value.Value;
        }

        var matched = rows.Select(row => row.Option.Candidates.FirstOrDefault(candidate =>
            !candidate.IsConflict && candidate.Values.Count == selected.Length &&
            candidate.Values.Select((value, index) => EqualsNormalized(value, selected[index])).All(x => x))).ToArray();
        if (_requireExistingCandidates && matched.Any(candidate => candidate == null))
        {
            Configurator.Infrastructure.ModernDialog.Show(
                "Выбранная комбинация отсутствует среди допустимых замен для части строк.",
                "Замена ссылок", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var (row, exact) in rows.Zip(matched))
        {

            if (exact != null)
            {
                row.Combo.SelectedItem = exact;
                continue;
            }

            // The selected parameters are independent values. They do not have to exist
            // as a pre-built pair in the candidate list. Build the replacement explicitly;
            // conflict/unique validation remains the responsibility of the deletion service.
            var template = row.Option.Candidates.FirstOrDefault();
            var synthetic = new ReplacementCandidate
            {
                Display = string.Join(" · ", selected.Select(x => x?.ToString() ?? "")),
                Values = selected.ToList(),
                AutoAssignments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            };
            row.Combo.ItemsSource = new[] { synthetic };
            row.Combo.SelectedIndex = 0;
            UpdateCandidateStatus(row);
        }
    }

    private static bool EqualsNormalized(object left, object right) =>
        string.Equals(left?.ToString() ?? "", right?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

    private static DataTemplate CreateParameterItemTemplate()
    {
        var template = new DataTemplate(typeof(ParameterChoice));
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding(nameof(ParameterChoice.Display)));
        template.VisualTree = factory;
        return template;
    }

    private static DataTemplate CreateCandidateItemTemplate(IReadOnlyList<string> columns)
    {
        var template = new DataTemplate(typeof(ReplacementCandidate));
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        var label = new Binding(".") { Converter = new CandidateLabelConverter(columns) };
        factory.SetBinding(TextBlock.TextProperty, label);
        factory.SetBinding(ToolTipService.ToolTipProperty, label);
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        template.VisualTree = factory;
        return template;
    }

    private sealed class CandidateLabelConverter(IReadOnlyList<string> columns) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not ReplacementCandidate candidate) return string.Empty;
            if (!string.IsNullOrWhiteSpace(candidate.Display)) return candidate.Display;
            var key = candidate.DisplayKey.Count > 0 ? candidate.DisplayKey : columns.Zip(candidate.Values)
                .ToDictionary(pair => pair.First, pair => pair.Second);
            return DependencyRecordLabel.Format(candidate.Display, key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var result = new List<ReferenceReplacement>();
        foreach (var row in _rows)
        {
            if (row.Combo?.SelectedItem is not ReplacementCandidate selected)
            {
                Configurator.Infrastructure.ModernDialog.Show("Для каждой зависимости выберите новое значение.", "Замена ссылок", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
            if (row.Option.Hit.SourceKey.Count == 0)
            {
                Configurator.Infrastructure.ModernDialog.Show($"Не удалось определить ключ зависимой записи в таблице {row.Option.Hit.SourceTable}.", "Замена ссылок", MessageBoxButton.OK, MessageBoxImage.Error); return;
            }
            if (selected.IsConflict)
            {
                Configurator.Infrastructure.ModernDialog.Show($"Выбранное значение создаёт конфликт уникальности в {row.Option.Hit.SourceTable}.\n{selected.ConflictDescription}\n\nВыберите другое значение или используйте автоматически предложенное переназначение Record.", "Конфликт уникальности", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
            result.Add(new ReferenceReplacement
            {
                SourceTable = row.Option.Hit.SourceTable,
                SourceColumns = row.Option.Hit.Relation.SourceColumns.ToList(),
                NewValues = selected.Values.ToList(),
                TargetValues = selected.TargetValues,
                KeyColumns = row.Option.Hit.SourceKey.Keys.ToList(),
                KeyValues = row.Option.Hit.SourceKey.Values.ToList(),
                ExpectedOldValues = row.Option.Hit.SourceReferenceValues,
                AdditionalAssignments = selected.AutoAssignments
            });
        }
        Replacements = result;
        DialogResult = true;
    }

    private static void UpdateCandidateStatus(Row row)
    {
        if (row.Status == null) return;
        if (row.Combo?.SelectedItem is not ReplacementCandidate selected) { row.Status.Text = string.Empty; return; }
        if (selected.IsConflict) { row.Status.Text = "⚠ " + selected.ConflictDescription; return; }
        row.Status.Text = string.Empty;
    }

    private static string ShortTableName(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) return string.Empty;
        var text = table.Trim('[', ']');
        var parts = text.Split('.');
        return parts[^1].Trim('[', ']');
    }

    private sealed class Row
    {
        public Row(DependencyOption option) => Option = option;
        public DependencyOption Option { get; }
        public ComboBox Combo { get; set; }
        public TextBlock Status { get; set; }
    }

    private sealed class GroupParameterChoice
    {
        public GroupParameterChoice(string column, ComboBox combo)
        {
            Column = column;
            Combo = combo;
        }
        public string Column { get; }
        public ComboBox Combo { get; }
    }

    private sealed class ParameterChoice
    {
        public ParameterChoice(object value, string display, bool isPlc)
        {
            Value = value;
            var raw = value?.ToString() ?? "<NULL>";
            Display = string.IsNullOrWhiteSpace(display) ? raw : isPlc ? display : $"{raw}  —  {display}";
        }
        public object Value { get; }
        public string Display { get; }
    }

    public sealed class DependencyOption
    {
        public DependencyHit Hit { get; init; } = new();
        public object CurrentValue { get; init; }
        public IReadOnlyList<ReplacementCandidate> Candidates { get; init; } = Array.Empty<ReplacementCandidate>();
    }
}
