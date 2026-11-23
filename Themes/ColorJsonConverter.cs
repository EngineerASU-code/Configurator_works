using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Configurator.Themes;

public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } value)
            throw new JsonException("Цвет темы должен быть строкой #RRGGBB или #AARRGGBB.");
        value = value.TrimStart('#');
        return value.Length switch
        {
            6 => Color.FromRgb(Parse(value, 0), Parse(value, 2), Parse(value, 4)),
            8 => Color.FromArgb(Parse(value, 0), Parse(value, 2), Parse(value, 4), Parse(value, 6)),
            _ => throw new JsonException("Цвет темы должен быть строкой #RRGGBB или #AARRGGBB.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");

    private static byte Parse(string value, int start) =>
        byte.Parse(value.Substring(start, 2), System.Globalization.NumberStyles.HexNumber);
}
