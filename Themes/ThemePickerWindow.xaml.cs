#nullable enable
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using Configurator.Themes;

namespace Configurator
{
    public partial class ThemePickerWindow : Window
    {
        private IReadOnlyList<ThemePreset> _projectThemes = Array.Empty<ThemePreset>();
        public System.Action<ThemePreset>? SaveProjectTheme { get; set; }

        public ThemePickerWindow()
        {
            InitializeComponent();
            HighlightActiveCard();
        }

        public void SetProjectThemes(IEnumerable<ThemePreset> themes, System.Action<ThemePreset>? save)
        {
            _projectThemes = themes.Select(theme => theme.Clone()).ToArray();
            SaveProjectTheme = save;
            ProjectThemesList.ItemsSource = _projectThemes;
            ProjectThemesList.DisplayMemberPath = nameof(ThemePreset.Name);
            BtnSaveProjectTheme.IsEnabled = save is not null;
        }

        private void ProjectThemesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ProjectThemesList.SelectedItem is not ThemePreset preset) return;
            ThemeManager.ApplyTheme(preset.Clone());
            HighlightActiveCard();
        }

        private void BtnSaveProjectTheme_Click(object sender, RoutedEventArgs e)
        {
            if (SaveProjectTheme is null) return;
            var preset = ThemeManager.CurrentPreset.Clone();
            SaveProjectTheme(preset);
            var themes = _projectThemes.Where(item => !item.Name.Equals(preset.Name, System.StringComparison.OrdinalIgnoreCase))
                .Append(preset).ToArray();
            _projectThemes = themes;
            ProjectThemesList.ItemsSource = themes;
        }

        private void HighlightActiveCard()
        {
            string currentName = ThemeManager.CurrentPreset?.Name ?? "Светлая";

            CardLight.BorderBrush = currentName == "Светлая"
                ? new SolidColorBrush(Color.FromRgb(44, 136, 204))
                : new SolidColorBrush(Color.FromRgb(204, 204, 204));

            CardDark.BorderBrush = currentName == "Тёмная"
                ? new SolidColorBrush(Color.FromRgb(44, 136, 204))
                : new SolidColorBrush(Color.FromRgb(102, 102, 102));
        }

        private void CardLight_Click(object sender, MouseButtonEventArgs e)
        {
            ThemeManager.ApplyTheme(ThemeManager.DefaultLight);
            HighlightActiveCard();
        }

        private void CardDark_Click(object sender, MouseButtonEventArgs e)
        {
            ThemeManager.ApplyTheme(ThemeManager.DefaultDark);
            HighlightActiveCard();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.ResetToDefault();
            HighlightActiveCard();
        }

        private void BtnCustomize_Click(object sender, RoutedEventArgs e)
        {
            var themeWindow = new ThemeWindow(ThemeManager.CurrentPreset ?? ThemeManager.DefaultLight);
            themeWindow.Owner = this;
            themeWindow.ShowDialog();
            HighlightActiveCard();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
