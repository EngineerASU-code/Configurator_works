#nullable disable
using System.Windows;
using System.Windows.Controls;

namespace Configurator;

public partial class ProgramManagementControl
{
    private int? ChooseStepDeletionReplacement(StepDeletionPreview preview)
    {
        var explanation = new TextBlock
        {
            Text = $"Выберите шаг, на который будут перенесены зависимости шага {preview.Number}. Входящих переходов: {preview.IncomingCount}. Параметров и значений рецептов: {preview.Children.Values.Sum(rows => rows.Count)}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var candidates = new ComboBox { ItemsSource = preview.Candidates };
        System.Windows.Automation.AutomationProperties.SetName(candidates, "Номер шага для замены");
        var confirm = new Button { Content = "Заменить и удалить", IsDefault = true, IsEnabled = false, MinWidth = 145, Margin = new Thickness(4) };
        confirm.SetResourceReference(StyleProperty, "BtnPrimary");
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(4) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, confirm }
        };
        var window = new Window
        {
            Owner = Window.GetWindow(this),
            Title = "Перенос зависимостей шага",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel { Margin = new Thickness(16), Children = { explanation, candidates, buttons } }
        };
        candidates.SelectionChanged += (_, _) => confirm.IsEnabled = candidates.SelectedItem is int;
        confirm.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true && candidates.SelectedItem is int number ? number : null;
    }
}
