#nullable enable
using Configurator;
using System.Data;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class MatrixAwlExporterTests
{
    [Theory]
    [InlineData("A", 1)]
    [InlineData("R", 1)]
    [InlineData("N", 1)]
    [InlineData("D", 2)]
    [InlineData("P", 2)]
    [InlineData("F", 2)]
    [InlineData("L", 4)]
    [InlineData("C", 4)]
    [InlineData("U", 8)]
    [InlineData("B", 16)]
    [InlineData("W", 64)]
    [InlineData("E", 128)]
    public void EncodeCommand_MapsEachLegacyCode(string code, byte expected)
        => Assert.Equal(expected, MatrixAwlExporter.EncodeCommand(code));

    [Fact]
    public void EncodeCommand_CombinesAliasesCaseAndWhitespace()
    {
        Assert.Equal((byte)3, MatrixAwlExporter.EncodeCommand(" a R\td "));
        Assert.Equal((byte)7, MatrixAwlExporter.EncodeCommand(" a R\td l "));
        Assert.Equal((byte)0, MatrixAwlExporter.EncodeCommand("  "));
    }

    [Fact]
    public void EncodeCommand_RejectsUnknownCodes()
        => Assert.Throws<InvalidOperationException>(() => MatrixAwlExporter.EncodeCommand("X"));

    [Fact]
    public async Task ValidateDeviceReferences_RejectsOrphanGroupBeforeOpeningSql()
    {
        var groups = new DataTable();
        groups.Columns.Add("DeviceList", typeof(int));
        groups.Rows.Add(1);
        var assignments = groups.Clone();
        assignments.Rows.Add(2);
        var repository = new ProgramSupportRepository("unused");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ValidateMatrixDeviceReferencesAsync(groups, new DataTable(), assignments,
                Array.Empty<MatrixDeviceClassOption>()));

        Assert.Contains("отсутствующую группу устройств 2", error.Message);
    }

    [Fact]
    public void Generate_MatchesZeroCommandMatrixShape()
    {
        var groups = Enumerable.Range(1, 11)
            .Select(group => new MatrixAwlDeviceGroup(group, new Dictionary<int, long> { [1] = group * 100_000L }))
            .ToArray();
        string awl = MatrixAwlExporter.Generate(new MatrixAwlSnapshot(
            23, 3023, Enumerable.Range(1, 11).ToArray(), Array.Empty<MatrixAwlCommand>(),
            Array.Empty<(int, int, string?)>(), groups));

        Assert.Contains("DATA_BLOCK DB3023\r\n", awl);
        Assert.Contains("TITLE = Matrix_23\r\n", awl);
        Assert.Contains("Cmd_count : INT  := 0;\r\n", awl);
        Assert.Contains("CMD : ARRAY  [1 .. 0, 1 .. 11 ] OF BYTE ;\r\n", awl);
        Assert.Contains("DevList : ARRAY  [1 .. 11, 1 .. 11 ] OF DINT ;\r\n", awl);
        Assert.Equal(121, awl.Split("   DevList[", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("   CMD[", awl);
        Assert.Contains("DevList[1, 2] := L#0;\r\n", awl);
    }

    [Fact]
    public void Generate_MatchesSeventyEightPositionFiftyFiveCommandShape()
    {
        var commands = Enumerable.Range(1, 55)
            .Select(number => new MatrixAwlCommand(number, 0, 0, 0))
            .ToArray();
        string awl = MatrixAwlExporter.Generate(new MatrixAwlSnapshot(
            27, 3027, Enumerable.Range(1, 78).ToArray(), commands,
            new (int Position, int CommandNumber, string? Code)[] { (1, 1, "A"), (78, 55, "E") },
            new[] { new MatrixAwlDeviceGroup(1, new Dictionary<int, long> { [1] = 101_506 }) }));

        Assert.Contains("Pos_count : INT  := 78;\r\n", awl);
        Assert.Contains("Cmd_count : INT  := 55;\r\n", awl);
        Assert.Contains("CMD : ARRAY  [1 .. 55, 1 .. 78 ] OF BYTE ;\r\n", awl);
        Assert.Contains("CMD[1, 1] := B#16#1;\r\n", awl);
        Assert.Contains("CMD[55, 78] := B#16#80;\r\n", awl);
        Assert.Equal(4_290, awl.Split("   CMD[", StringSplitOptions.None).Length - 1);
        Assert.Equal(78, awl.Split("   DevList[", StringSplitOptions.None).Length - 1);
        Assert.Contains("DevList[1, 78] := L#0;\r\n", awl);
    }

    [Fact]
    public void Generate_PreservesSingleCommandAndItsCells()
    {
        string awl = MatrixAwlExporter.Generate(new MatrixAwlSnapshot(
            23, 3023, new[] { 1, 2 },
            new[] { new MatrixAwlCommand(1, 0, 0, 0) },
            new (int Position, int CommandNumber, string? Code)[] { (1, 1, "A"), (2, 1, "D") },
            Array.Empty<MatrixAwlDeviceGroup>()));

        Assert.Contains("Cmd_count : INT  := 1;\r\n", awl);
        Assert.Contains("CMD : ARRAY  [1 .. 1, 1 .. 2 ] OF BYTE ;\r\n", awl);
        Assert.Contains("CMD[1, 1] := B#16#1;\r\n", awl);
        Assert.Contains("CMD[1, 2] := B#16#2;\r\n", awl);
    }

    [Fact]
    public void Generate_RejectsInvalidCommandBeforeReturningFileContent()
    {
        var snapshot = new MatrixAwlSnapshot(1, 1, new[] { 1 },
            new[] { new MatrixAwlCommand(1, 0, 0, 0) },
            new (int Position, int CommandNumber, string? Code)[] { (1, 1, "? ") }, Array.Empty<MatrixAwlDeviceGroup>());

        Assert.Throws<InvalidOperationException>(() => MatrixAwlExporter.Generate(snapshot));
    }
}
