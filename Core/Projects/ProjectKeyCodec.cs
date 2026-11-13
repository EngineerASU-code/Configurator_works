#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configurator.Core.Projects;

/// <summary>Versioned, unambiguous encoding for keys stored in project snapshots.</summary>
public static class ProjectKeyCodec
{
    public const string Prefix = "k1:";

    public static string Encode(ProjectTableSchema schema, IReadOnlyList<string> columns,
        Func<string, JsonNode?> valueFor)
    {
        var entries = new List<object>(columns.Count);
        foreach (var name in columns)
        {
            var column = schema.Columns.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Ключевой столбец «{name}» отсутствует в проверенной схеме.");
            var node = valueFor(column.Name);
            entries.Add(new { column = column.Name, type = column.DataType.ToLowerInvariant(), value = CanonicalValue(node, column.DataType) });
        }
        return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(entries));
    }

    public static JsonNode? ToNode(object? value) => value == null || value == DBNull.Value
        ? null
        : JsonSerializer.SerializeToNode(value);

    private static string? CanonicalValue(JsonNode? node, string dataType)
    {
        if (node == null) return null;
        var raw = node is JsonValue value ? value.ToString() : node.ToJsonString();
        switch (dataType.ToLowerInvariant())
        {
            case "tinyint": case "smallint": case "int": case "bigint":
                return decimal.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case "decimal": case "numeric": case "money": case "smallmoney":
                return decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture);
            case "float": case "real":
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            case "date": case "datetime": case "datetime2": case "smalldatetime": case "datetimeoffset":
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    return date.UtcTicks.ToString(CultureInfo.InvariantCulture);
                break;
            case "bit":
                if (bool.TryParse(raw, out var boolean)) return boolean ? "1" : "0";
                break;
        }
        return raw;
    }
}
