using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class AreaDisplayFormatterTests
{
    [Theory]
    [InlineData("091", "Production", "091 + Production")]
    [InlineData("091", "", "091")]
    [InlineData("091", null, "091")]
    [InlineData("091", "091", "091")]
    [InlineData("", "Production", "")]
    public void FormatsAreaWithoutChangingKey(string key, string name, string expected)
        => Assert.Equal(expected, AreaDisplayFormatter.Format(key, name));

    [Fact]
    public void ExtractsNameFromPreviouslyComposedDisplay()
        => Assert.Equal("Production", AreaDisplayFormatter.ExtractName("091", "091 - Production"));
}
