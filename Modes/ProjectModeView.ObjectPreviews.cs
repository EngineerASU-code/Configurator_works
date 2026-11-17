using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Configurator;

public partial class ProjectModeView
{
    private static readonly Dictionary<string, BitmapImage> ObjectPreviewImages = new(StringComparer.OrdinalIgnoreCase);

    private void UpdateObjectTypePreviewFromEditors()
    {
        if (_selectedClass == null || _selectedRow == null) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_selectedRow.Table.Columns.Contains("Type"))
            values["Type"] = GetText("Type");

        foreach (var control in FindEditorControls(ObjectEditor))
        {
            if (control.Tag is not string fieldName) continue;
            var definition = _selectedClass.Fields.FirstOrDefault(field => field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            bool isTypeOrLookupField = fieldName.Contains("Type", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(definition?.LookupTable)
                || !string.IsNullOrWhiteSpace(definition?.LookupKeyField);
            if (!isTypeOrLookupField || IsScopeField(fieldName)
                || fieldName.Equals("PLC_Class_Number", StringComparison.OrdinalIgnoreCase)
                || fieldName.Equals("PLCClassNumber", StringComparison.OrdinalIgnoreCase)
                || fieldName.Equals("PLCClasssNumber", StringComparison.OrdinalIgnoreCase))
                continue;

            if (control is ComboBox combo)
            {
                var selectedKey = Convert.ToString(combo.SelectedValue) ?? string.Empty;
                var selectedOption = combo.SelectedItem as LookupOption
                    ?? combo.Items.OfType<LookupOption>().FirstOrDefault(option => option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
                values[fieldName] = selectedOption?.Display ?? selectedKey;
            }
            else if (control is TextBox textBox)
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text)) values[fieldName] = textBox.Text;
            }
        }

        ApplyObjectTypePreview(_selectedClass.Name, values);
    }

    private void UpdateConfigurationTypePreview(DataRow row)
    {
        if (row == null || !ReferenceEquals(_selectedRow, row)) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.Table.Columns.Contains("Type"))
        {
            var typeEditor = FindEditorControls(ObjectEditor)
                .FirstOrDefault(control => control.Tag is string fieldName
                    && fieldName.Equals("Type", StringComparison.OrdinalIgnoreCase));

            if (typeEditor is ComboBox combo)
            {
                var option = combo.SelectedItem as LookupOption;
                var display = option?.Display?.Trim();
                if (!string.IsNullOrWhiteSpace(display)
                    && !display.Equals("(не задано)", StringComparison.OrdinalIgnoreCase))
                    values["Type"] = display;
                else if (!string.IsNullOrWhiteSpace(combo.SelectedValue?.ToString()))
                    values["Type"] = combo.SelectedValue.ToString();
            }
            else if (typeEditor is TextBox textBox)
            {
                values["Type"] = textBox.Text;
            }

            if (!values.ContainsKey("Type") && !row.IsNull("Type"))
                values["Type"] = Convert.ToString(row["Type"]) ?? string.Empty;
        }

        ApplyObjectTypePreview("PLC", values);
    }

    private void ApplyObjectTypePreview(string className, IReadOnlyDictionary<string, string> values)
    {
        string imageName = ResolveObjectPreviewImage(className, values);
        if (imageName != null)
        {
            try
            {
                ImgObjectTypePreview.Source = LoadObjectPreviewImage(imageName);
                ImgObjectTypePreview.Visibility = Visibility.Visible;
                TxtPreviewGlyph.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ObjectPreview] {imageName}: {ex.Message}");
            }
        }

        ImgObjectTypePreview.Source = null;
        ImgObjectTypePreview.Visibility = Visibility.Collapsed;
        TxtPreviewGlyph.Visibility = Visibility.Visible;
        TxtPreviewGlyph.Text = className?.Length > 3 ? className[..3].ToUpperInvariant() : className?.ToUpperInvariant() ?? "OBJ";
    }

    private static string ResolveObjectPreviewImage(string className, IReadOnlyDictionary<string, string> values) =>
        Core.Architecture.ObjectTypePreviewCatalog.Resolve(className, values);

    private static bool IsScopeField(string name) =>
        name.Equals("PLC", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Area", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AreaName", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Record", StringComparison.OrdinalIgnoreCase);

    private static BitmapImage LoadObjectPreviewImage(string imageName)
    {
        if (ObjectPreviewImages.TryGetValue(imageName, out var cached)) return cached;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://application:,,,/Images/ObjectPreviews/{imageName}", UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        ObjectPreviewImages[imageName] = image;
        return image;
    }
}
