namespace Configurator.Themes
{
    public class ThemePreset
    {
        public string Name { get; set; } = "Светлая";

        // Shared design tokens (DIP). Values are exposed as application resources by ThemeManager.
        public double FontXs { get; set; } = 11;
        public double FontSm { get; set; } = 12;
        public double FontMd { get; set; } = 13;
        public double FontLg { get; set; } = 16;
        public double FontXl { get; set; } = 20;
        public double FontTitle { get; set; } = 24;
        public double RadiusSm { get; set; } = 6;
        public double RadiusMd { get; set; } = 9;
        public double RadiusLg { get; set; } = 12;
        public double SpaceXs { get; set; } = 4;
        public double SpaceSm { get; set; } = 8;
        public double SpaceMd { get; set; } = 12;
        public double SpaceLg { get; set; } = 16;
        public double SpaceXl { get; set; } = 24;

        public System.Windows.Media.Color Base { get; set; } = System.Windows.Media.Color.FromRgb(245, 245, 245);
        public System.Windows.Media.Color Panel { get; set; } = System.Windows.Media.Color.FromRgb(255, 255, 255);
        public System.Windows.Media.Color Border { get; set; } = System.Windows.Media.Color.FromRgb(204, 204, 204);
        public System.Windows.Media.Color Text { get; set; } = System.Windows.Media.Color.FromRgb(51, 51, 51);
        public System.Windows.Media.Color Accent { get; set; } = System.Windows.Media.Color.FromRgb(44, 136, 204);
        public System.Windows.Media.Color Hover { get; set; } = System.Windows.Media.Color.FromRgb(235, 240, 248);
        public System.Windows.Media.Color Selected { get; set; } = System.Windows.Media.Color.FromRgb(220, 232, 245);
        public System.Windows.Media.Color ComboBoxBackground { get; set; } = System.Windows.Media.Color.FromRgb(255, 255, 255);
        public System.Windows.Media.Color ComboBoxForeground { get; set; } = System.Windows.Media.Color.FromRgb(31, 41, 55);
        public System.Windows.Media.Color ComboBoxBorder { get; set; } = System.Windows.Media.Color.FromRgb(217, 222, 231);
        public System.Windows.Media.Color ComboBoxPopupBackground { get; set; } = System.Windows.Media.Color.FromRgb(255, 255, 255);
        public System.Windows.Media.Color ComboBoxPopupForeground { get; set; } = System.Windows.Media.Color.FromRgb(31, 41, 55);
        public System.Windows.Media.Color ComboBoxItemHoverBackground { get; set; } = System.Windows.Media.Color.FromRgb(240, 244, 250);
        public System.Windows.Media.Color ComboBoxItemHoverForeground { get; set; } = System.Windows.Media.Color.FromRgb(31, 41, 55);
        public System.Windows.Media.Color ComboBoxItemSelectedBackground { get; set; } = System.Windows.Media.Color.FromRgb(231, 240, 255);
        public System.Windows.Media.Color ComboBoxItemSelectedForeground { get; set; } = System.Windows.Media.Color.FromRgb(31, 41, 55);
        public System.Windows.Media.Color ComboBoxBorderHover { get; set; } = System.Windows.Media.Color.FromRgb(52, 120, 246);
        public System.Windows.Media.Color ComboBoxBorderFocus { get; set; } = System.Windows.Media.Color.FromRgb(52, 120, 246);
        public System.Windows.Media.Color ComboBoxDisabledBackground { get; set; } = System.Windows.Media.Color.FromRgb(235, 237, 241);
        public System.Windows.Media.Color ComboBoxDisabledForeground { get; set; } = System.Windows.Media.Color.FromRgb(120, 125, 135);


        public ThemePreset Clone() => new ThemePreset
        {
            Name = Name,
            FontXs = FontXs, FontSm = FontSm, FontMd = FontMd, FontLg = FontLg, FontXl = FontXl, FontTitle = FontTitle,
            RadiusSm = RadiusSm, RadiusMd = RadiusMd, RadiusLg = RadiusLg,
            SpaceXs = SpaceXs, SpaceSm = SpaceSm, SpaceMd = SpaceMd, SpaceLg = SpaceLg, SpaceXl = SpaceXl,
            Base = Base,
            Panel = Panel,
            Border = Border,
            Text = Text,
            Accent = Accent,
            Hover = Hover,
            Selected = Selected,
            ComboBoxBackground = ComboBoxBackground, ComboBoxForeground = ComboBoxForeground, ComboBoxBorder = ComboBoxBorder,
            ComboBoxPopupBackground = ComboBoxPopupBackground, ComboBoxPopupForeground = ComboBoxPopupForeground,
            ComboBoxItemHoverBackground = ComboBoxItemHoverBackground, ComboBoxItemHoverForeground = ComboBoxItemHoverForeground,
            ComboBoxItemSelectedBackground = ComboBoxItemSelectedBackground, ComboBoxItemSelectedForeground = ComboBoxItemSelectedForeground,
            ComboBoxBorderHover = ComboBoxBorderHover, ComboBoxBorderFocus = ComboBoxBorderFocus,
            ComboBoxDisabledBackground = ComboBoxDisabledBackground, ComboBoxDisabledForeground = ComboBoxDisabledForeground
        };
    }
}
