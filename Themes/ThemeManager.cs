using Configurator;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Configurator.Themes
{
    public static class ThemeManager
    {
        public static ThemePreset CurrentPreset { get; set; } = new ThemePreset();

        // Workbench palettes from microsoft/vscode, extensions/theme-defaults/themes.
        public static readonly ThemePreset DefaultLight = Palette("Light", "#F8F8F8", "#FFFFFF", "#E5E5E5", "#3B3B3B", "#005FB8", "#F2F2F2", "#E8E8E8");
        public static readonly ThemePreset DefaultDark = Palette("Dark", "#181818", "#1F1F1F", "#2B2B2B", "#CCCCCC", "#0078D4", "#2A2D2E", "#37373D");

        private static readonly ThemePreset SystemTheme = new() { Name = "System" };

        public static IReadOnlyList<ThemePreset> BuiltIn { get; } = Array.AsReadOnly(new[]
        {
            SystemTheme, DefaultLight, DefaultDark,
            Palette("Abyss", "#10192C", "#000C18", "#2B3F60", "#C0D2F2", "#25589C", "#162540", "#26395E"),
            Palette("Solarized Light", "#EEE8D5", "#FDF6E3", "#C9C2AE", "#43565C", "#007D77", "#E8E3D0", "#D9E6D8")
        });

        private static ThemePreset ResolveSystemPalette()
        {
            try
            {
                var value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is 0 ? DefaultDark : DefaultLight;
            }
            catch (System.Security.SecurityException) { return DefaultLight; }
            catch (UnauthorizedAccessException) { return DefaultLight; }
        }

        private static ThemePreset Palette(string name, string surface, string panel, string border, string text,
            string accent, string hover, string selected)
        {
            Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
            return new ThemePreset
            {
                Name = name, Base = C(surface), Panel = C(panel), Border = C(border), Text = C(text),
                Accent = C(accent), Hover = C(hover), Selected = C(selected),
                ComboBoxBackground = C(panel), ComboBoxForeground = C(text), ComboBoxBorder = C(border),
                ComboBoxPopupBackground = C(panel), ComboBoxPopupForeground = C(text),
                ComboBoxItemHoverBackground = C(hover), ComboBoxItemHoverForeground = C(text),
                ComboBoxItemSelectedBackground = C(selected), ComboBoxItemSelectedForeground = C(text),
                ComboBoxBorderHover = C(accent), ComboBoxBorderFocus = C(accent),
                ComboBoxDisabledBackground = C(surface), ComboBoxDisabledForeground = C(text)
            };
        }

        public static void ApplyTheme(ThemePreset preset)
        {
            CurrentPreset = preset;
            ApplyPalette(preset == SystemTheme ? ResolveSystemPalette() : preset, preset.Name);
        }

        private static void ApplyPalette(ThemePreset preset, string schemeName)
        {

            var res = global::System.Windows.Application.Current.Resources;

            // Centralized typography, radius and spacing tokens.
            res["FontXs"] = preset.FontXs; res["FontSm"] = preset.FontSm; res["FontMd"] = preset.FontMd;
            res["FontLg"] = preset.FontLg; res["FontXl"] = preset.FontXl; res["FontTitle"] = preset.FontTitle;
            res["RadiusSm"] = new CornerRadius(preset.RadiusSm);
            res["RadiusMd"] = new CornerRadius(preset.RadiusMd);
            res["RadiusLg"] = new CornerRadius(preset.RadiusLg);
            res["SpaceXs"] = preset.SpaceXs; res["SpaceSm"] = preset.SpaceSm; res["SpaceMd"] = preset.SpaceMd;
            res["SpaceLg"] = preset.SpaceLg; res["SpaceXl"] = preset.SpaceXl;

            res["BrushBase"] = new SolidColorBrush(preset.Base);
            res["BrushPanel"] = new SolidColorBrush(preset.Panel);
            res["BrushBorder"] = new SolidColorBrush(preset.Border);
            res["BrushText"] = new SolidColorBrush(preset.Text);
            res["BrushTextSecondary"] = new SolidColorBrush(Color.FromRgb((byte)(preset.Text.R * 0.85 + preset.Base.R * 0.15), (byte)(preset.Text.G * 0.85 + preset.Base.G * 0.15), (byte)(preset.Text.B * 0.85 + preset.Base.B * 0.15)));
            res["BrushAccent"] = new SolidColorBrush(preset.Accent);
            res["BrushHover"] = new SolidColorBrush(preset.Hover);
            res["BrushSelected"] = new SolidColorBrush(preset.Selected);
            res["BrushComboBoxBackground"] = new SolidColorBrush(preset.ComboBoxBackground);
            res["BrushComboBoxForeground"] = new SolidColorBrush(preset.ComboBoxForeground);
            res["BrushComboBoxBorder"] = new SolidColorBrush(preset.ComboBoxBorder);
            res["BrushComboBoxPopupBackground"] = new SolidColorBrush(preset.ComboBoxPopupBackground);
            res["BrushComboBoxPopupForeground"] = new SolidColorBrush(preset.ComboBoxPopupForeground);
            res["BrushComboBoxItemHoverBackground"] = new SolidColorBrush(preset.ComboBoxItemHoverBackground);
            res["BrushComboBoxItemHoverForeground"] = new SolidColorBrush(preset.ComboBoxItemHoverForeground);
            res["BrushComboBoxItemSelectedBackground"] = new SolidColorBrush(preset.ComboBoxItemSelectedBackground);
            res["BrushComboBoxItemSelectedForeground"] = new SolidColorBrush(preset.ComboBoxItemSelectedForeground);
            res["BrushComboBoxBorderHover"] = new SolidColorBrush(preset.ComboBoxBorderHover);
            res["BrushComboBoxBorderFocus"] = new SolidColorBrush(preset.ComboBoxBorderFocus);
            res["BrushComboBoxDisabledBackground"] = new SolidColorBrush(preset.ComboBoxDisabledBackground);
            res["BrushComboBoxDisabledForeground"] = new SolidColorBrush(preset.ComboBoxDisabledForeground);

            // Сохраняем полный пресет
            var settings = AppSettings.Load();
            settings.LastThemeName = schemeName;
            settings.Base = AppSettings.ColorToHex(preset.Base);
            settings.Panel = AppSettings.ColorToHex(preset.Panel);
            settings.Border = AppSettings.ColorToHex(preset.Border);
            settings.Text = AppSettings.ColorToHex(preset.Text);
            settings.Accent = AppSettings.ColorToHex(preset.Accent);
            settings.Hover = AppSettings.ColorToHex(preset.Hover);
            settings.Selected = AppSettings.ColorToHex(preset.Selected);
            settings.ComboBoxBackground = AppSettings.ColorToHex(preset.ComboBoxBackground);
            settings.ComboBoxForeground = AppSettings.ColorToHex(preset.ComboBoxForeground);
            settings.ComboBoxBorder = AppSettings.ColorToHex(preset.ComboBoxBorder);
            settings.ComboBoxPopupBackground = AppSettings.ColorToHex(preset.ComboBoxPopupBackground);
            settings.ComboBoxPopupForeground = AppSettings.ColorToHex(preset.ComboBoxPopupForeground);
            settings.ComboBoxItemHoverBackground = AppSettings.ColorToHex(preset.ComboBoxItemHoverBackground);
            settings.ComboBoxItemHoverForeground = AppSettings.ColorToHex(preset.ComboBoxItemHoverForeground);
            settings.ComboBoxItemSelectedBackground = AppSettings.ColorToHex(preset.ComboBoxItemSelectedBackground);
            settings.ComboBoxItemSelectedForeground = AppSettings.ColorToHex(preset.ComboBoxItemSelectedForeground);
            settings.ComboBoxBorderHover = AppSettings.ColorToHex(preset.ComboBoxBorderHover);
            settings.ComboBoxBorderFocus = AppSettings.ColorToHex(preset.ComboBoxBorderFocus);
            settings.ComboBoxDisabledBackground = AppSettings.ColorToHex(preset.ComboBoxDisabledBackground);
            settings.ComboBoxDisabledForeground = AppSettings.ColorToHex(preset.ComboBoxDisabledForeground);
            AppSettings.Save(settings);

            foreach (Window w in global::System.Windows.Application.Current.Windows)
            {
                w.InvalidateVisual();
            }
        }

        public static void ResetToDefault()
        {
            // Сброс к светлой теме
            ApplyTheme(DefaultLight);
        }

        public static void InitializeDefault()
        {
            string name = AppSettings.Load().LastThemeName;
            var preset = BuiltIn.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));
            ApplyTheme(preset ?? (name switch
            {
                "Тёмная" or "Dark Modern" or "Dark+" or "High Contrast Dark" => DefaultDark,
                "Светлая" or "Light Modern" or "Light+" or "High Contrast Light" => DefaultLight,
                _ => SystemTheme
            }));
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }

        private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
            var application = global::System.Windows.Application.Current;
            application?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CurrentPreset == SystemTheme) ApplyPalette(ResolveSystemPalette(), SystemTheme.Name);
            }));
        }
    }
}
