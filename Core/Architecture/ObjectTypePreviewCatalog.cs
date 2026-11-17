using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Configurator.Core.Architecture;

/// <summary>Maps displayed equipment types to shared preview resources.</summary>
public static class ObjectTypePreviewCatalog
{
    private static readonly Regex LookupRecordPrefix = new(@"^\s*\d+\s*[·.]\s*", RegexOptions.Compiled);
    private static readonly Regex LookupKeySuffix = new(@"\s*\[\s*\d+\s*\]\s*$", RegexOptions.Compiled);

    public static string Resolve(string className, IReadOnlyDictionary<string, string> values)
    {
        var normalizedClass = (className ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedClass is "AREA" or "PROGRAM" or "STATUS" or "STEP" or "MATRIX") return null;

        string type = Normalize(GetValue(values, "Type"));
        switch (normalizedClass)
        {
            case "PLC":
                if (type is "s71500" or "s1500") return "plc-s7-1500.png";
                if (type is "s7300" or "s300") return "plc-s7-300.png";
                if (type is "s7400" or "s400") return "plc-s400.png";
                return null;

            case "VALVE":
            case "VALVES":
                if (type == "discret") return "valve-discret.png";
                if (type == "asi") return "valve-asi.png";
                return "valve.png";

            case "MOTOR":
                return type == "discretio" ? "motor-discret-io.png" : "motor.png";
            case "DI":
                return type == "no" ? "di-no.png" : "di.png";
            case "AI":
                return type == "default" ? "ai-default.png" : "ai.png";
            case "AO":
                return type == "siemensperipherie" ? "ao-siemens-peripherie.png" : "ao.png";
            case "DSV":
                return "dsv.png";
            case "DO":
                return "do.png";
            case "PID":
                return "pid.png";
            default:
                return null;
        }
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string name)
    {
        if (values == null) return string.Empty;
        foreach (var pair in values)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private static string Normalize(string value)
    {
        var display = (value ?? string.Empty).Trim();
        const string currentValueSuffix = "· текущее значение";
        if (display.EndsWith(currentValueSuffix, StringComparison.OrdinalIgnoreCase))
            display = display[..^currentValueSuffix.Length].TrimEnd();
        display = LookupRecordPrefix.Replace(display, string.Empty);
        display = LookupKeySuffix.Replace(display, string.Empty);
        return new string(display.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
