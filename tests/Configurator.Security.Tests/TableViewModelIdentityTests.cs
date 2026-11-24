using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class TableViewModelIdentityTests
{
    [Theory]
    [InlineData("timestamp", false, true)]
    [InlineData("TIMESTAMP", false, true)]
    [InlineData("rowversion", false, true)]
    [InlineData("text", false, false)]
    [InlineData("ntext", false, false)]
    [InlineData("image", false, false)]
    [InlineData("xml", false, false)]
    [InlineData("varchar", true, true)]
    [InlineData("int", false, true)]
    [InlineData("nvarchar", false, true)]
    public void ValueIdentityIncludesAllComparableColumns(
        string dataType,
        bool isComputed,
        bool expected)
    {
        var column = new ColumnInfo { Name = "Value", DataType = dataType, IsComputed = isComputed };

        Assert.Equal(expected, TableViewModel.IsValueIdentityColumn(column));
    }
}
