using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

internal sealed record AssemblyRecordOption(int Record, string Name)
{
    public override string ToString() => $"{Record} · {Name}";
}

public partial class AssemblyEditWindow : Window
{
    internal enum ActionKind { Keep, Replace, Copy }
    internal sealed class Choice
    {
        internal string Id;
        internal ActionKind Action;
        internal int Record;
        internal int Original;
        internal IReadOnlyList<AssemblyRecordOption> Options;
        internal ComboBox Mode, Target;
        internal TextBox Number;
    }

    internal List<Choice> Entries { get; } = new();
    internal int ProgramRecord { get; private set; }
    internal string ProgramName => NewName.Text.Trim();
    internal Func<Task> Commit { get; set; }
    private readonly bool _duplicate;
    private bool _saving;
    private readonly Dictionary<string, HashSet<int>> _suggestedRecords = new();

    internal AssemblyEditWindow(bool duplicate, string name, int next, string summary)
    {
        InitializeComponent();
        _duplicate = duplicate;
        Title = duplicate ? "Дублировать Object" : "Заменить привязку";
        Heading.Text = $"{Title} · {name}";
        RootFields.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        NewName.Text = name + " — копия";
        NewRecord.Text = next.ToString();
        Apply.Content = duplicate ? "Создать копию" : "Сохранить привязку";
        Summary.Text = summary;
        Closing += (_, args) => { if (_saving) args.Cancel = true; };
    }

    internal void AddChoice(string id, string title, int original, IReadOnlyList<AssemblyRecordOption> options)
    {
        var mode = new ComboBox { ItemsSource = _duplicate ? new[] { "Оставить", "Выбрать другой", "Создать копию" } : new[] { "Оставить", "Заменить" }, SelectedIndex = _duplicate || Entries.Count > 0 ? 0 : 1 };
        var target = new ComboBox { ItemsSource = options.Where(p => p.Record != original).ToList(), IsTextSearchEnabled = true, MaxDropDownHeight = 260 };
        string scope = id.Substring(0, Math.Max(0, id.LastIndexOf('/')));
        if (!_suggestedRecords.TryGetValue(scope, out var reserved)) _suggestedRecords[scope] = reserved = new HashSet<int>();
        int next = FirstFree(options.Select(o => o.Record).Concat(reserved));
        reserved.Add(next);
        var number = new TextBox { Text = next.ToString(), Padding = new Thickness(5) };
        var grid = new Grid { Margin = new Thickness(0,3,0,3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        grid.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        foreach (var control in new Control[] {mode,target,number})
        {
            Grid.SetColumn(control, control == mode ? 1 : 2);
            control.Margin = new Thickness(6,0,0,0);
            grid.Children.Add(control);
        }
        void ShowEditor() { target.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; number.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
        mode.SelectionChanged += (_, _) => ShowEditor();
        ShowEditor();
        Choices.Children.Add(grid);
        Entries.Add(new Choice { Id = id, Original = original, Options = options, Mode = mode, Target = target, Number = number });
    }

    internal static int FirstFree(IEnumerable<int> records)
    {
        var used = records.ToHashSet();
        int i = 1;
        while (used.Contains(i)) i++;
        return i;
    }

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        try
        {
            if (_duplicate && (ProgramName.Length == 0 || !int.TryParse(NewRecord.Text, out var n) || n < 1))
                throw new InvalidOperationException("Заполните имя и положительный Record программы.");
            if (_duplicate) ProgramRecord = int.Parse(NewRecord.Text);
            foreach (var entry in Entries)
            {
                entry.Action = (ActionKind)entry.Mode.SelectedIndex;
                entry.Record = entry.Action switch
                {
                    ActionKind.Keep => entry.Original,
                    ActionKind.Replace when entry.Target.SelectedItem is AssemblyRecordOption o => o.Record,
                    ActionKind.Copy when int.TryParse(entry.Number.Text, out int r) && r > 0 && entry.Options.All(x => x.Record != r) => r,
                    _ => throw new InvalidOperationException("Выберите замену или укажите свободный Record для копии.")
                };
            }
            if (!_duplicate && Entries.All(e => e.Action == ActionKind.Keep)) throw new InvalidOperationException("Выберите привязку для замены.");
            _saving = true;
            IsEnabled = false;
            await Commit();
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = Infrastructure.UserErrorMessage.Format(ex); }
        finally { _saving = false; IsEnabled = true; }
    }
}
