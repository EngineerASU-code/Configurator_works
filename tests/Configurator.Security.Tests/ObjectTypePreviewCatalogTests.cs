using System.Collections.Generic;
using Configurator.Core.Architecture;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ObjectTypePreviewCatalogTests
{
    [Theory]
    [InlineData("1 · S7-1500", "plc-s7-1500.png")]
    [InlineData("S7300", "plc-s7-300.png")]
    [InlineData("S400", "plc-s400.png")]
    [InlineData("s7 300", "plc-s7-300.png")]
    public void Resolve_PlcAliasesToTypeSpecificPreview(string type, string expected) =>
        Assert.Equal(expected, Resolve("PLC", ("Type", type)));

    [Theory]
    [InlineData("1 · Discret", "MS_Type", "1 · КВ", "valve-kv-gea.png")]
    [InlineData("Discret", "MS_Type", "КВ", "valve-kv-gea.png")]
    [InlineData("Discret [1]", "", "", "valve-discret.png")]
    [InlineData("2 · AS-i", "", "", "valve-asi.png")]
    public void Resolve_ValveTypeAndModel(string type, string modelField, string model, string expected)
    {
        var values = new Dictionary<string, string> { ["Type"] = type };
        if (modelField.Length > 0) values[modelField] = model;
        Assert.Equal(expected, ObjectTypePreviewCatalog.Resolve("Valve", values));
    }

    [Theory]
    [InlineData("Motor", "1 · Discret IO", "motor-discret-io.png")]
    [InlineData("AI", "1 · Default", "ai-default.png")]
    [InlineData("AO", "1 · SIEMENS peripherie", "ao-siemens-peripherie.png")]
    public void Resolve_DocumentedTypeToSpecificPreview(string className, string type, string expected) =>
        Assert.Equal(expected, Resolve(className, ("Type", type)));

    [Theory]
    [InlineData("Motor", "Discret IO", "PU", "motor-pu.png")]
    [InlineData("Motor", "Discret IO", "2 · Mix [2]", "motor-mix.png")]
    [InlineData("Valve", "AS-i", "RV", "valve-rv-gea.png")]
    [InlineData("DI", "NO", "FS · Flow switch", "di-fs.png")]
    [InlineData("DI", "LSH", "", "di-lsh.png")]
    [InlineData("DI", "LSH", "LSL", "di-lsl.png")]
    [InlineData("DI", "Концевик на ёмкости", "", "di-limit.png")]
    [InlineData("DI", "Импульс расходомера", "", "di-pulse.png")]
    public void Resolve_PhysicalDeviceFromTypeAndSubtype(string name, string type, string subtype, string expected) =>
        Assert.Equal(expected, ObjectTypePreviewCatalog.Resolve(name,
            new Dictionary<string, string> { ["Type"] = type, ["MS_Type"] = subtype }));

    [Fact]
    public void Resolve_UnknownTypesUseClassFallbackOrNoPlcImage()
    {
        Assert.Equal("valve.png", Resolve("Valve", ("Type", "unknown valve")));
        Assert.Equal("dsv.png", Resolve("DSV", ("Type", "unknown DSV")));
        Assert.Equal("motor.png", Resolve("Motor", ("Type", "unknown motor")));
        Assert.Null(Resolve("DI", ("Type", "unknown DI")));
        Assert.Equal("do.png", Resolve("DO", ("Type", "unknown DO")));
        Assert.Equal("ai.png", Resolve("AI", ("Type", "unknown AI")));
        Assert.Equal("ao.png", Resolve("AO", ("Type", "unknown AO")));
        Assert.Equal("pid.png", Resolve("PID", ("Type", "unknown PID")));
        Assert.Null(Resolve("PLC", ("Type", "unknown PLC")));
    }

    [Theory]
    [InlineData("Area")]
    [InlineData("Program")]
    [InlineData("Status")]
    [InlineData("Step")]
    [InlineData("Matrix")]
    public void Resolve_ExcludedClassesHaveNoEquipmentImage(string className) =>
        Assert.Null(ObjectTypePreviewCatalog.Resolve(className, new Dictionary<string, string>()));

    [Fact]
    public void Resolve_IgnoresUnrelatedEngineeringValuesAndReusesSameAsset()
    {
        var first = new Dictionary<string, string> { ["Type"] = "Default [1]", ["Units"] = "%" };
        var second = new Dictionary<string, string> { ["type"] = "default", ["Units"] = "bar" };

        Assert.Equal("ai-default.png", ObjectTypePreviewCatalog.Resolve("AI", first));
        Assert.Equal(ObjectTypePreviewCatalog.Resolve("AI", first), ObjectTypePreviewCatalog.Resolve("AI", second));
    }

    private static string Resolve(string className, (string Name, string Value) field)
        => ObjectTypePreviewCatalog.Resolve(className, new Dictionary<string, string> { [field.Name] = field.Value });
}
