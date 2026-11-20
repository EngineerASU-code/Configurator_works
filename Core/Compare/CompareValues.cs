#nullable enable
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configurator.Core.Compare;

public static class CompareValues
{
    public static CompareStatus Compare(
        JsonNode? a,
        JsonNode? b,
        string? dataTypeA = null,
        string? dataTypeB = null)
    {
        var typeA = TypeFamily(dataTypeA);
        var typeB = TypeFamily(dataTypeB);
        if (typeA is not null && typeB is not null && typeA != typeB)
            return CompareStatus.NotComparable;
        if (a is null || b is null)
            return a is null && b is null ? CompareStatus.Equal : CompareStatus.Changed;

        var type = typeA ?? typeB;
        if (type is null)
            return SameJsonKindAndValue(a, b) ? CompareStatus.Equal : CompareStatus.Changed;

        var rawA = RawValue(a);
        var rawB = RawValue(b);
        var comparison = type switch
        {
            "integer" => ParseAndCompare(IsNumber(a) && IsNumber(b), IntegerEquals(rawA, rawB, out var integersEqual), integersEqual),
            "decimal" => ParseAndCompare(IsNumber(a) && IsNumber(b), DecimalEquals(rawA, rawB, out var decimalsEqual), decimalsEqual),
            "float" => ParseAndCompare(IsNumber(a) && IsNumber(b), FloatingEquals(rawA, rawB, out var floatsEqual), floatsEqual),
            "boolean" => ParseAndCompare(IsBooleanLike(a) && IsBooleanLike(b), BooleanEquals(rawA, rawB, out var booleansEqual), booleansEqual),
            "date" => ParseAndCompare(IsString(a) && IsString(b), DateEquals(rawA, rawB, out var datesEqual), datesEqual),
            "time" => ParseAndCompare(IsString(a) && IsString(b), TimeEquals(rawA, rawB, out var timesEqual), timesEqual),
            "datetime" => ParseAndCompare(IsString(a) && IsString(b), DateTimeEquals(rawA, rawB, out var datetimesEqual), datetimesEqual),
            "datetimeoffset" => ParseAndCompare(IsString(a) && IsString(b), DateTimeOffsetEquals(rawA, rawB, out var offsetsEqual), offsetsEqual),
            "binary" => ParseAndCompare(IsString(a) && IsString(b), BinaryEquals(rawA, rawB, out var binariesEqual), binariesEqual),
            "guid" => ParseAndCompare(IsString(a) && IsString(b), GuidEquals(rawA, rawB, out var guidsEqual), guidsEqual),
            "string" => IsString(a) && IsString(b)
                ? Status(string.Equals(rawA, rawB, StringComparison.Ordinal))
                : CompareStatus.NotComparable,
            _ => CompareStatus.NotComparable
        };

        return comparison;
    }

    private static string? TypeFamily(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return null;
        var type = dataType.Trim().ToLowerInvariant();
        if (type.StartsWith("[") && type.EndsWith("]")) type = type[1..^1];
        return type switch
        {
            "tinyint" or "smallint" or "int" or "bigint" => "integer",
            "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
            "float" or "real" => "float",
            "bit" or "boolean" or "bool" => "boolean",
            "date" => "date",
            "time" => "time",
            "datetimeoffset" => "datetimeoffset",
            "datetime" or "datetime2" or "smalldatetime" => "datetime",
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "binary",
            "uniqueidentifier" or "uuid" => "guid",
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" => "string",
            _ => type
        };
    }

    private static string RawValue(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return node.ToJsonString();
    }

    private static bool IsString(JsonNode value) =>
        value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String;

    private static bool IsNumber(JsonNode value) =>
        value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.Number;

    private static bool IsBooleanLike(JsonNode value) =>
        value is JsonValue jsonValue && jsonValue.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number;

    private static bool SameJsonKindAndValue(JsonNode a, JsonNode b)
    {
        if (a is JsonValue valueA && b is JsonValue valueB)
        {
            var kindA = valueA.GetValueKind();
            var kindB = valueB.GetValueKind();
            return kindA == kindB &&
                string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
        }

        if (a is JsonObject && b is JsonObject || a is JsonArray && b is JsonArray)
            return JsonNode.DeepEquals(a, b);

        return false;
    }

    private static bool IntegerEquals(string a, string b, out bool equal)
    {
        var parsedA = BigInteger.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valueA);
        var parsedB = BigInteger.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valueB);
        equal = parsedA && parsedB && valueA == valueB;
        return parsedA && parsedB;
    }

    private static bool DecimalEquals(string a, string b, out bool equal)
    {
        equal = false;
        if (!TryCanonicalDecimal(a, out var canonicalA) || !TryCanonicalDecimal(b, out var canonicalB)) return false;
        equal = string.Equals(canonicalA, canonicalB, StringComparison.Ordinal);
        return true;
    }

    private static bool TryCanonicalDecimal(string value, out string canonical)
    {
        canonical = string.Empty;
        value = value.Trim();
        if (value.Length == 0) return false;

        var exponentAt = value.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? value : value[..exponentAt];
        var exponent = 0;
        if (exponentAt >= 0 && (!int.TryParse(value[(exponentAt + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || Math.Abs((long)exponent) > 1000))
            return false;

        var negative = mantissa.StartsWith('-');
        if (negative || mantissa.StartsWith('+')) mantissa = mantissa[1..];
        var pointAt = mantissa.IndexOf('.');
        var fractionalDigits = pointAt < 0 ? 0 : mantissa.Length - pointAt - 1;
        if (pointAt >= 0) mantissa = mantissa.Remove(pointAt, 1);
        if (mantissa.Length == 0 || mantissa.Any(x => x is < '0' or > '9')) return false;

        var digits = mantissa.TrimStart('0');
        var scale = fractionalDigits - exponent;
        if (digits.Length == 0)
        {
            canonical = "0";
            return true;
        }

        while (scale > 0 && digits.EndsWith('0'))
        {
            digits = digits[..^1];
            scale--;
        }
        if (scale < 0)
        {
            digits += new string('0', -scale);
            scale = 0;
        }
        canonical = $"{(negative ? "-" : string.Empty)}{digits}:{scale}";
        return true;
    }

    private static bool FloatingEquals(string a, string b, out bool equal)
    {
        var parsedA = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var valueA);
        var parsedB = double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var valueB);
        equal = parsedA && parsedB && valueA.Equals(valueB);
        return parsedA && parsedB;
    }

    private static bool BooleanEquals(string a, string b, out bool equal)
    {
        var parsedA = TryBoolean(a, out var valueA);
        var parsedB = TryBoolean(b, out var valueB);
        equal = parsedA && parsedB && valueA == valueB;
        return parsedA && parsedB;
    }

    private static bool TryBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result)) return true;
        if (value == "1") { result = true; return true; }
        if (value == "0") { result = false; return true; }
        return false;
    }

    private static bool DateEquals(string a, string b, out bool equal)
    {
        var parsedA = DateOnly.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueA);
        var parsedB = DateOnly.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueB);
        equal = parsedA && parsedB && valueA == valueB;
        return parsedA && parsedB;
    }

    private static bool TimeEquals(string a, string b, out bool equal)
    {
        var parsedA = TimeOnly.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueA);
        var parsedB = TimeOnly.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueB);
        equal = parsedA && parsedB && valueA == valueB;
        return parsedA && parsedB;
    }

    private static bool DateTimeEquals(string a, string b, out bool equal)
    {
        var parsedA = DateTime.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var valueA);
        var parsedB = DateTime.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var valueB);
        equal = parsedA && parsedB && valueA.Ticks == valueB.Ticks;
        return parsedA && parsedB;
    }

    private static bool DateTimeOffsetEquals(string a, string b, out bool equal)
    {
        var parsedA = DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueA);
        var parsedB = DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueB);
        equal = parsedA && parsedB && valueA.EqualsExact(valueB);
        return parsedA && parsedB;
    }

    private static bool BinaryEquals(string a, string b, out bool equal)
    {
        try
        {
            equal = Convert.FromBase64String(a).AsSpan().SequenceEqual(Convert.FromBase64String(b));
            return true;
        }
        catch (FormatException)
        {
            equal = false;
            return false;
        }
    }

    private static bool GuidEquals(string a, string b, out bool equal)
    {
        var parsedA = Guid.TryParse(a, out var valueA);
        var parsedB = Guid.TryParse(b, out var valueB);
        equal = parsedA && parsedB && valueA == valueB;
        return parsedA && parsedB;
    }

    private static CompareStatus ParseAndCompare(bool correctJsonKind, bool parsed, bool equal) =>
        correctJsonKind && parsed ? Status(equal) : CompareStatus.NotComparable;

    private static CompareStatus Status(bool equal) => equal ? CompareStatus.Equal : CompareStatus.Changed;
}
