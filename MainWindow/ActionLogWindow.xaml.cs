using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Configurator;

public partial class ActionLogWindow : Window
{
    private readonly ChangeJournalService _journal;
    private readonly Func<ChangeJournalEntry, Task> _restore;

    internal Func<IEnumerable<ChangeJournalEntry>> ReadEntries { get; set; }

    public ActionLogWindow(ChangeJournalService journal, Func<ChangeJournalEntry, Task> restore)
    {
        InitializeComponent();
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        Refresh();
    }

    public void RefreshEntries() => Refresh();

    private void Refresh()
    {
        var entries = (ReadEntries?.Invoke() ?? _journal.LoadRecent(500)).ToList();
        LogList.ItemsSource = entries;
        TxtCount.Text = $"Операций: {entries.Count}";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ChangeJournalEntry entry) return;
        var result = Configurator.Infrastructure.ModernDialog.Show(
            $"Отменить действие?\n\n{entry.DisplayDescription}\n{entry.Database}",
            "Отмена действия", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            await _restore(entry);
            Refresh();
        }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show("Не удалось восстановить операцию: " + Configurator.Infrastructure.UserErrorMessage.Format(ex), "Восстановление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Require(Permission.RestoreJournal)) return;
        if (Configurator.Infrastructure.ModernDialog.Show(this,
            "Удалить историю операций текущего проекта? Возможность отменить эти операции через журнал будет потеряна.",
            "Очистка журнала", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _journal.Clear(); }
        catch (Exception ex)
        {
            Configurator.Infrastructure.ModernDialog.Show(this, Configurator.Infrastructure.UserErrorMessage.Format(ex),
                "Не удалось очистить журнал", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
