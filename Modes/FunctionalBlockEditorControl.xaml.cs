#nullable enable
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Configurator.Core.Projects;

namespace Configurator;

public partial class FunctionalBlockEditorControl : UserControl
{
    private List<FunctionalBlockDefinition> _blocks = new();
    private FunctionalBlockStore? _store;
    private FunctionalBlockDefinition? _selected;
    private bool _loading;
    private bool _isNew;
    private bool _dirty;

    public FunctionalBlockEditorControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UserAuthorizationService.Changed += AuthorizationChanged;
            UpdatePermissions();
        };
        Unloaded += (_, _) => UserAuthorizationService.Changed -= AuthorizationChanged;
        UpdatePermissions();
    }

    public event Action<string>? ProjectDataChanged;

    public void ConfigureOffline(ConfiguratorProject project) => Configure(new FunctionalBlockStore(project));

    public void ConfigureOnline(string server, string database)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            _store = null;
            _blocks.Clear();
            BlocksList.ItemsSource = null;
            ShowSelection(null);
            StatusText.Text = "Выберите Online-проект в режиме «Объекты проекта», чтобы открыть его блоки.";
            return;
        }
        Configure(new FunctionalBlockStore(server, database));
    }

    private void Configure(FunctionalBlockStore store)
    {
        _store = store;
        try
        {
            _blocks = store.Load();
            BlocksList.ItemsSource = _blocks;
            BlocksList.SelectedItem = null;
            ShowSelection(null);
            StatusText.Text = _blocks.Count == 0 ? "В этом проекте пока нет функциональных блоков." : $"Блоков: {_blocks.Count}";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _store = null;
            _blocks = new();
            BlocksList.ItemsSource = null;
            ShowSelection(null);
            StatusText.Text = $"Не удалось прочитать блоки: {ex.Message}";
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!UserAuthorizationService.Has(Permission.Mode4Manage) || !ConfirmLeaveEditor() || _store == null) return;
        _selected = new FunctionalBlockDefinition();
        _isNew = true;
        _dirty = false;
        ShowSelection(_selected);
        NameBox.Focus();
        StatusText.Text = "Новый блок не сохранён.";
    }

    private void BlocksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var requested = BlocksList.SelectedItem as FunctionalBlockDefinition;
        if (!ConfirmLeaveEditor())
        {
            _loading = true;
            BlocksList.SelectedItem = _selected;
            _loading = false;
            return;
        }
        // Saving the previous selection refreshes ItemsSource, so restore the user's requested row.
        _loading = true;
        BlocksList.SelectedItem = requested;
        _loading = false;
        _selected = requested;
        _isNew = false;
        _dirty = false;
        ShowSelection(_selected);
        StatusText.Text = _selected == null ? $"Блоков: {_blocks.Count}" : "Изменения ещё не сохранены.";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || !SaveCurrent()) return;
        string name = GetUniqueCopyName(_selected.Name);
        var copy = _selected.Copy(name);
        _blocks.Add(copy);
        if (!Persist($"Дублирован блок «{name}»"))
        {
            _blocks.Remove(copy);
            return;
        }
        _loading = true;
        BlocksList.ItemsSource = null;
        BlocksList.ItemsSource = _blocks;
        BlocksList.SelectedItem = copy;
        _loading = false;
        _selected = copy;
        _isNew = false;
        _dirty = false;
        ShowSelection(copy);
        StatusText.Text = $"Создана копия «{copy.Name}».";
    }

    private void BlocksList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None ||
            !DeleteButton.IsEnabled || !ReferenceEquals(BlocksList.SelectedItem, _selected)) return;

        e.Handled = true;
        if (!e.IsRepeat) Delete_Click(sender, e);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _isNew || !UserAuthorizationService.Has(Permission.Mode4Manage) || !ConfirmLeaveEditor()) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Удалить блок «{_selected.Name}»?", "Удаление блока",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deleted = _selected;
        _blocks.Remove(deleted);
        if (!Persist($"Удалён блок «{deleted.Name}»"))
        {
            _blocks.Add(deleted);
            return;
        }
        _loading = true;
        BlocksList.ItemsSource = null;
        BlocksList.ItemsSource = _blocks;
        BlocksList.SelectedItem = null;
        _loading = false;
        _selected = null;
        _isNew = false;
        _dirty = false;
        ShowSelection(null);
        StatusText.Text = $"Блок «{deleted.Name}» удалён.";
    }

    private bool SaveCurrent()
    {
        if (_selected == null || _store == null || !UserAuthorizationService.Has(Permission.Mode4Manage)) return false;
        string name = NameBox.Text.Trim();
        if (name.Length is < 1 or > 120)
        {
            ShowValidation("Укажите имя блока длиной от 1 до 120 символов.");
            return false;
        }
        if (_blocks.Any(block => !ReferenceEquals(block, _selected)
                && string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowValidation("Блок с таким именем уже существует в проекте.");
            return false;
        }
        if (DescriptionBox.Text.Length > 4000 || InterfaceBox.Text.Length > 12000 || SourceBox.Text.Length > 250000)
        {
            ShowValidation("Описание, интерфейс или текст реализации превышает допустимый размер.");
            return false;
        }

        if (!SaveWithRollback(_blocks, _selected, _isNew, name, DescriptionBox.Text,
                InterfaceBox.Text, SourceBox.Text, () => Persist(_isNew ? $"Добавлен блок «{name}»" : $"Изменён блок «{name}»"))) return false;
        _isNew = false;
        _dirty = false;
        UpdatePermissions();
        _loading = true;
        BlocksList.ItemsSource = null;
        BlocksList.ItemsSource = _blocks;
        BlocksList.SelectedItem = _selected;
        _loading = false;
        StatusText.Text = $"Блок «{name}» сохранён.";
        return true;
    }

    internal static bool SaveWithRollback(List<FunctionalBlockDefinition> blocks, FunctionalBlockDefinition selected,
        bool isNew, string name, string description, string interfaceText, string sourceText, Func<bool> persist)
    {
        var original = selected.Copy(selected.Name);
        bool saved = false;
        try
        {
            selected.Name = name;
            selected.Description = description;
            selected.InterfaceText = interfaceText;
            selected.SourceText = sourceText;
            if (isNew) blocks.Add(selected);
            saved = persist();
            return saved;
        }
        finally
        {
            if (!saved)
            {
                if (isNew) blocks.Remove(selected);
                selected.Name = original.Name;
                selected.Description = original.Description;
                selected.InterfaceText = original.InterfaceText;
                selected.SourceText = original.SourceText;
            }
        }
    }

    private bool Persist(string description)
    {
        try
        {
            _store!.Save(_blocks);
            ProjectDataChanged?.Invoke(description);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Не удалось сохранить блоки: {ex.Message}";
            return false;
        }
    }

    private string GetUniqueCopyName(string sourceName)
    {
        string suffix = " (копия)";
        string stem = sourceName.Length + suffix.Length <= 120
            ? sourceName + suffix
            : sourceName[..(120 - suffix.Length)] + suffix;
        string candidate = stem;
        for (int number = 2; _blocks.Any(block => string.Equals(block.Name, candidate, StringComparison.OrdinalIgnoreCase)); number++)
        {
            string numberedSuffix = $" (копия {number})";
            candidate = sourceName[..Math.Min(sourceName.Length, 120 - numberedSuffix.Length)] + numberedSuffix;
        }
        return candidate;
    }

    private bool ConfirmLeaveEditor()
    {
        if (!_dirty) return true;
        var result = MessageBox.Show(Window.GetWindow(this), "Сохранить изменения текущего блока?", "Несохранённые изменения",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return SaveCurrent();
        if (_isNew)
        {
            _selected = null;
            _isNew = false;
        }
        _dirty = false;
        ShowSelection(_selected);
        return true;
    }

    public bool ConfirmPendingEdits() => ConfirmLeaveEditor();

    private void ShowSelection(FunctionalBlockDefinition? block)
    {
        _loading = true;
        bool selected = block != null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        EditorPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ActionPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = block?.Name ?? string.Empty;
        DescriptionBox.Text = block?.Description ?? string.Empty;
        InterfaceBox.Text = block?.InterfaceText ?? string.Empty;
        SourceBox.Text = block?.SourceText ?? string.Empty;
        _loading = false;
        UpdatePermissions();
    }

    private void UpdatePermissions()
    {
        bool canManage = UserAuthorizationService.Has(Permission.Mode4Manage);
        AddButton.IsEnabled = canManage && _store != null;
        NameBox.IsEnabled = DescriptionBox.IsEnabled = InterfaceBox.IsEnabled = SourceBox.IsEnabled = canManage;
        SaveButton.IsEnabled = canManage && _selected != null && _store != null;
        DuplicateButton.IsEnabled = canManage && _selected != null && !_isNew;
        DeleteButton.IsEnabled = canManage && _selected != null && !_isNew;
    }

    private void AuthorizationChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdatePermissions();
        else Dispatcher.BeginInvoke(UpdatePermissions);
    }

    private void ShowValidation(string message)
    {
        StatusText.Text = message;
        NameBox.Focus();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selected == null) return;
        _dirty = true;
        StatusText.Text = "Изменения ещё не сохранены.";
    }
}
