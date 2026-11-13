using Configurator;
using System.Windows;
using System.Windows.Media;

namespace Configurator.Themes
{
    public static class ThemeManager
    {
        public static ThemePreset CurrentPreset { get; set; } = new ThemePreset();

        // Стандартные пресеты
        public static readonly ThemePreset DefaultLight = new ThemePreset
        {
            Name = "Светлая",
            Base = Color.FromRgb(245, 247, 250),
            Panel = Color.FromRgb(255, 255, 255),
            Border = Color.FromRgb(217, 222, 231),
            Text = Color.FromRgb(31, 41, 55),
            Accent = Color.FromRgb(52, 120, 246),
            Hover = Color.FromRgb(240, 244, 250),
            Selected = Color.FromRgb(231, 240, 255),
            ComboBoxBackground = Color.FromRgb(255,255,255), ComboBoxForeground = Color.FromRgb(31,41,55), ComboBoxBorder = Color.FromRgb(217,222,231),
            ComboBoxPopupBackground = Color.FromRgb(255,255,255), ComboBoxPopupForeground = Color.FromRgb(31,41,55),
            ComboBoxItemHoverBackground = Color.FromRgb(240,244,250), ComboBoxItemHoverForeground = Color.FromRgb(31,41,55),
            ComboBoxItemSelectedBackground = Color.FromRgb(231,240,255), ComboBoxItemSelectedForeground = Color.FromRgb(31,41,55),
            ComboBoxBorderHover = Color.FromRgb(52,120,246), ComboBoxBorderFocus = Color.FromRgb(52,120,246),
            ComboBoxDisabledBackground = Color.FromRgb(235,237,241), ComboBoxDisabledForeground = Color.FromRgb(120,125,135)
        };

        public static readonly ThemePreset DefaultDark = new ThemePreset
        {
            Name = "Тёмная",
            Base = Color.FromRgb(41, 45, 51),
            Panel = Color.FromRgb(53, 59, 68),
            Border = Color.FromRgb(80, 89, 101),
            Text = Color.FromRgb(240, 242, 245),
            Accent = Color.FromRgb(109, 158, 255),
            Hover = Color.FromRgb(66, 74, 85),
            Selected = Color.FromRgb(52, 79, 120),
            ComboBoxBackground = Color.FromRgb(48,54,63), ComboBoxForeground = Color.FromRgb(245,247,250), ComboBoxBorder = Color.FromRgb(92,101,114),
            ComboBoxPopupBackground = Color.FromRgb(48,54,63), ComboBoxPopupForeground = Color.FromRgb(245,247,250),
            ComboBoxItemHoverBackground = Color.FromRgb(70,82,99), ComboBoxItemHoverForeground = Color.FromRgb(255,255,255),
            ComboBoxItemSelectedBackground = Color.FromRgb(55,91,140), ComboBoxItemSelectedForeground = Color.FromRgb(255,255,255),
            ComboBoxBorderHover = Color.FromRgb(109,158,255), ComboBoxBorderFocus = Color.FromRgb(109,158,255),
            ComboBoxDisabledBackground = Color.FromRgb(62,67,76), ComboBoxDisabledForeground = Color.FromRgb(170,176,185)
        };

        public static void ApplyTheme(ThemePreset preset)
        {
            CurrentPreset = preset;

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
            res["BrushTextSecondary"] = new SolidColorBrush(Color.FromArgb(preset.Text.A, (byte)(preset.Text.R * 0.62), (byte)(preset.Text.G * 0.62), (byte)(preset.Text.B * 0.62)));
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
            settings.LastThemeName = preset.Name;
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
            var settings = AppSettings.Load();

            // Если есть сохранённые цвета — загружаем полный пресет
            if (!string.IsNullOrEmpty(settings.LastThemeName))
            {
                bool darkTheme = settings.LastThemeName.Equals("Тёмная", System.StringComparison.OrdinalIgnoreCase);
                // Existing installations persist the old near-black palette. Upgrade that exact
                // built-in palette to the new medium-dark preset, while preserving custom themes.
                if (darkTheme && string.Equals(settings.Base, "#17191D", System.StringComparison.OrdinalIgnoreCase))
                {
                    ApplyTheme(DefaultDark);
                    return;
                }
                var defaults = darkTheme ? DefaultDark : DefaultLight;
                ApplyTheme(new ThemePreset
                {
                    Name = settings.LastThemeName,
                    Base = AppSettings.HexToColor(settings.Base),
                    Panel = AppSettings.HexToColor(settings.Panel),
                    Border = AppSettings.HexToColor(settings.Border),
                    Text = AppSettings.HexToColor(settings.Text),
                    Accent = AppSettings.HexToColor(settings.Accent),
                    Hover = AppSettings.HexToColor(settings.Hover),
                    Selected = AppSettings.HexToColor(settings.Selected),
                    ComboBoxBackground = AppSettings.HexToColor(settings.ComboBoxBackground), ComboBoxForeground = AppSettings.HexToColor(settings.ComboBoxForeground), ComboBoxBorder = AppSettings.HexToColor(settings.ComboBoxBorder),
                    ComboBoxPopupBackground = AppSettings.HexToColor(settings.ComboBoxPopupBackground), ComboBoxPopupForeground = AppSettings.HexToColor(settings.ComboBoxPopupForeground),
                    ComboBoxItemHoverBackground = AppSettings.HexToColor(settings.ComboBoxItemHoverBackground), ComboBoxItemHoverForeground = AppSettings.HexToColor(settings.ComboBoxItemHoverForeground),
                    ComboBoxItemSelectedBackground = AppSettings.HexToColor(settings.ComboBoxItemSelectedBackground), ComboBoxItemSelectedForeground = AppSettings.HexToColor(settings.ComboBoxItemSelectedForeground),
                    ComboBoxBorderHover = AppSettings.HexToColor(settings.ComboBoxBorderHover), ComboBoxBorderFocus = AppSettings.HexToColor(settings.ComboBoxBorderFocus),
                    ComboBoxDisabledBackground = AppSettings.HexToColor(settings.ComboBoxDisabledBackground), ComboBoxDisabledForeground = AppSettings.HexToColor(settings.ComboBoxDisabledForeground)
                });
            }
            else
            {
                ApplyTheme(DefaultLight);
            }
        }
    }
}
