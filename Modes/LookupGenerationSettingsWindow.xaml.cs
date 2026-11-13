using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;

namespace Configurator;

public partial class LookupGenerationSettingsWindow : Window
{
    private readonly ConfiguratorProject _project;
    private readonly ObservableCollection<LookupGenerationRule> _rules;

    public LookupGenerationSettingsWindow(ConfiguratorProject project)
    {
        InitializeComponent();
        _project = project ?? throw new ArgumentNullException(nameof(project));
        var settings = LookupGenerationSettingsStore.Load(project);
        ChkAutomaticDiscovery.IsChecked = settings.AutomaticDiscoveryEnabled;
        _rules = new ObservableCollection<LookupGenerationRule>(settings.Rules.Select(Clone));
        RulesGrid.ItemsSource = _rules;
    }

    private static LookupGenerationRule Clone(LookupGenerationRule r) => new()
    {
        Id = r.Id, Enabled = r.Enabled, ClassName = r.ClassName, SourceTable = r.SourceTable,
        SourceField = r.SourceField, LookupTable = r.LookupTable, KeyField = r.KeyField,
        DisplayField = r.DisplayField, IsUserDefined = r.IsUserDefined
    };

    private LookupGenerationSettings BuildSettings() => new()
    {
        SchemaVersion = 1,
        AutomaticDiscoveryEnabled = ChkAutomaticDiscovery.IsChecked == true,
        Rules = _rules.Select(Clone).ToList()
    };

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new LookupGenerationRule { IsUserDefined = true, ClassName = "Новый класс", SourceField = "Type", KeyField = "Record", DisplayField = "NameL2" };
        _rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = RulesGrid.SelectedItems.Cast<object>().OfType<LookupGenerationRule>().ToList();
        foreach (var rule in selected)
        {
            if (rule.IsUserDefined) _rules.Remove(rule);
            else rule.Enabled = false;
        }
        RulesGrid.Items.Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Импорт настроек справочников", Filter = "Настройки справочников (*.json)|*.json|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = LookupGenerationSettingsStore.Import(dialog.FileName);
            var choice = Configurator.Infrastructure.ModernDialog.Show(this,
                $"Импортировано правил: {imported.Rules.Count}.\nТекущие правила будут заменены импортированными. Продолжить?",
                "Импорт настроек", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            _rules.Clear();
            foreach (var rule in imported.Rules) _rules.Add(Clone(rule));
            ChkAutomaticDiscovery.IsChecked = imported.AutomaticDiscoveryEnabled;
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(this, ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RulesGrid.CommitEdit(); RulesGrid.CommitEdit();
            var dialog = new SaveFileDialog { Title = "Экспорт настроек справочников", Filter = "Настройки справочников (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = "LookupGenerationSettings.json" };
            if (dialog.ShowDialog(this) == true) LookupGenerationSettingsStore.Export(dialog.FileName, BuildSettings());
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(this, ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RulesGrid.CommitEdit(); RulesGrid.CommitEdit();
            LookupGenerationSettingsStore.Save(_project, BuildSettings());
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { Configurator.Infrastructure.ModernDialog.Show(this, ex.Message, "Настройки не сохранены", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
