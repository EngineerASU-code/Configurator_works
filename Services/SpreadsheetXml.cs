using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Configurator;

internal static class SpreadsheetXml
{
    internal static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // Preserve the exporters' XML filtering and escape text once, at the cell boundary.
    internal static string Esc(string value) => SecurityElement.Escape(new string((value ?? "").Where(c =>
        c == '\t' || c == '\n' || c == '\r' || (c >= ' ' && c <= '\uD7FF') ||
        (c >= '\uE000' && c <= '\uFFFD') || char.IsSurrogate(c)).ToArray())) ?? "";

    internal static string ColumnName(int column)
    {
        var result = "";
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    internal static string Cell(object value, int row, int column, int styleIndex = 0,
        IFormatProvider textFormat = null)
    {
        string reference = ColumnName(column) + row;
        string style = styleIndex > 0 ? $" s=\"{styleIndex}\"" : "";
        if (value == null || value == DBNull.Value) return $"<c r=\"{reference}\"{style}/>";
        if (value is bool b) return $"<c r=\"{reference}\"{style} t=\"b\"><v>{(b ? 1 : 0)}</v></c>";
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return $"<c r=\"{reference}\"{style}><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>";
        return $"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(Convert.ToString(value, textFormat))}</t></is></c>";
    }
}
