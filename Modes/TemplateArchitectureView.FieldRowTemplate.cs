using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Configurator;

public partial class TemplateArchitectureView
{
    private static readonly bool _fieldRowTemplateRegistered = RegisterFieldRowTemplateHandler();

    private static bool RegisterFieldRowTemplateHandler()
    {
        EventManager.RegisterClassHandler(typeof(TemplateArchitectureView), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(EnsureFieldRowTemplate));
        return true;
    }

    private static void EnsureFieldRowTemplate(object sender, RoutedEventArgs e)
    {
        var view = (TemplateArchitectureView)sender;
        if (view.Resources.Contains("FieldRowTemplate")) return;

        const string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid Margin='0,2,0,2'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='180'/>
      <ColumnDefinition Width='115'/>
      <ColumnDefinition Width='75'/>
      <ColumnDefinition Width='75'/>
      <ColumnDefinition Width='75'/>
      <ColumnDefinition Width='220'/>
      <ColumnDefinition Width='1.6*'/>
    </Grid.ColumnDefinitions>
    <TextBox IsReadOnly='True' Grid.Column='0' Text='{Binding Name, Mode=OneWay}' Height='28' Margin='2'/>
    <TextBox IsReadOnly='True' Grid.Column='1' Text='{Binding DataType, Mode=OneWay}' Height='28' Margin='2'/>
    <TextBox IsReadOnly='True' Grid.Column='2' Text='{Binding Size, Mode=OneWay}' Height='28' Margin='2'/>
    <TextBox IsReadOnly='True' Grid.Column='3' Text='{Binding Offset, Mode=OneWay}' Height='28' Margin='2'/>
    <TextBox IsReadOnly='True' Grid.Column='4' Text='{Binding BitOffset, Mode=OneWay}' Height='28' Margin='2'/>
    <CheckBox IsEnabled='False' Grid.Column='5' IsChecked='{Binding Nullable, Mode=OneWay}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
    <TextBox IsReadOnly='True' Grid.Column='6' Text='{Binding Description, Mode=OneWay}' Height='28' Margin='2' TextWrapping='Wrap'/>
  </Grid>
</DataTemplate>";

        view.Resources["FieldRowTemplate"] = (DataTemplate)XamlReader.Parse(xaml);
    }
}
