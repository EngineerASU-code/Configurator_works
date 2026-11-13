using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class TableViewModelIdentityTests
{
    [Theory]
    [InlineData("timestamp", false, false)]
    [InlineData("TIMESTAMP", false, false)]
    [InlineData("rowversion", false, false)]
    [InlineData("text", false, false)]
    [InlineData("ntext", false, false)]
    [InlineData("image", false, false)]
    [InlineData("xml", false, false)]
    [InlineData("varchar", true, false)]
    [InlineData("int", false, true)]
    [InlineData("nvarchar", false, true)]
    public void ValueIdentityUsesComparableNonComputedColumns(
        string dataType,
        bool isComputed,
        bool expected)
    {
        var column = new ColumnInfo { Name = "Value", DataType = dataType, IsComputed = isComputed };

        Assert.Equal(expected, TableViewModel.IsValueIdentityColumn(column));
    }
}
