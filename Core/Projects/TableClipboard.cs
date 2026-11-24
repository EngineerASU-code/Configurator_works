using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.FileIO;

namespace Configurator.Core.Projects;

internal static class TableClipboard
{
    internal static List<string[]> Read(string text)
    {
        using var parser = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters("\t");
        var rows = new List<string[]>();
        while (!parser.EndOfData)
            rows.Add(parser.ReadFields());

        if (rows.Count == 0 || rows.Any(row => row.Length != rows[0].Length))
            throw new FormatException("Скопируйте прямоугольный диапазон ячеек.");
        return rows;
    }

    internal static string Write(IEnumerable<IEnumerable<object>> rows) =>
        string.Join("\r\n", rows.Select(row => string.Join("\t", row.Select(value =>
        {
            var text = value switch
            {
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                byte[] bytes => "0x" + Convert.ToHexString(bytes),
                _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""
            };
            return text.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0
                ? text
                : "\"" + text.Replace("\"", "\"\"") + "\"";
        }))));
}
