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
    [InlineData("Valve")]
    [InlineData("Motor")]
    [InlineData("DI")]
    [InlineData("DO")]
    [InlineData("AI")]
    [InlineData("AO")]
    [InlineData("DSV")]
    [InlineData("PID")]
    [InlineData("Area")]
    [InlineData("Program")]
    [InlineData("Status")]
    [InlineData("Step")]
    [InlineData("Matrix")]
    public void Resolve_ClassesWithoutAssetsUseNoEquipmentImage(string className) =>
        Assert.Null(Resolve(className, ("Type", "S7300")));
    private static string Resolve(string className, (string Name, string Value) field)
        => ObjectTypePreviewCatalog.Resolve(className, new Dictionary<string, string> { [field.Name] = field.Value });
}
