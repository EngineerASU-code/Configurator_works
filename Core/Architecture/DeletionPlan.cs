using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configurator.Core.Architecture;

public sealed class DeletionRequest
{
    public string TableName { get; init; } = "";
    public IReadOnlyDictionary<string, object> Key { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, object> ExpectedValues { get; init; } = new Dictionary<string, object>();
}

public sealed class DependencyHit
{
    public string SourceTable { get; init; } = "";
    public IReadOnlyDictionary<string, object> SourceKey { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, object> SourceValues { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, object> SourceReferenceValues { get; init; } = new Dictionary<string, object>();
    public string SourceColumnDescription { get; init; } = "";
    public string TargetTable { get; init; } = "";
    internal string SourceName { get; init; } = "";
    internal string TargetName { get; init; } = "";
    internal IReadOnlyDictionary<string, object> TargetKey { get; init; } = new Dictionary<string, object>();
    public DatabaseRelation Relation { get; init; } = new();
}

internal static class DependencyRecordLabel
{
    private static readonly JsonSerializerOptions StringKeyOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] NameColumns =
        ["Name", "NameL1", "Name_L1", "NameL2", "Name_L2", "AreaNameL2", "AreaNameL1", "AreaName", "DescriptionL1"];

    internal static string Name(IReadOnlyDictionary<string, object> values) =>
        Name(column => values.TryGetValue(column, out var value) ? value : null);

    internal static string Name(JsonObject values) => Name(column =>
        values.FirstOrDefault(pair => pair.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value);

    private static string Name(Func<string, object> getValue)
    {
        foreach (var column in NameColumns)
        {
            var raw = getValue(column);
            var value = raw is JsonValue node && node.TryGetValue<string>(out var text)
                ? text : Convert.ToString(raw, CultureInfo.CurrentCulture);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    internal static string Format(string name, IEnumerable<KeyValuePair<string, object>> key)
    {
        var identity = string.Join(" · ", key.Select(pair => $"{pair.Key} = {FormatValue(pair.Value)}"));
        if (string.IsNullOrWhiteSpace(name)) return identity;
        return identity.Length == 0 ? name : $"{name} ({identity})";
    }

    private static string FormatValue(object value) => value switch
    {
        null or DBNull => "<NULL>",
        string text => JsonSerializer.Serialize(text, StringKeyOptions),
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}

public sealed class DeletionPlan
{
    public DeletionRequest Request { get; init; } = new();
    public List<DependencyHit> Dependencies { get; } = new();
    public List<string> ValidationErrors { get; } = new();
    public bool RequiresReplacement => Dependencies.Count > 0;
    public bool CanCommit => ValidationErrors.Count == 0 && Dependencies.TrueForAll(x => x.Relation.IsUsableForDeletionProtection);
}
