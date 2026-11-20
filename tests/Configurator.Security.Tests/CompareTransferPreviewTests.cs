#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class CompareTransferPreviewTests
{
    [Fact]
    public async Task SelectedCellOffersBothDirectionsAndPackagesRemainUnchanged()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var beforeA = System.IO.File.ReadAllBytes(files.A);
        var beforeB = System.IO.File.ReadAllBytes(files.B);
        var result = await CompareEngine.CompareAsync(a, b);
        var cell = FindDifference(result, "Pressure");

        var ab = CompareTransferPreview.Evaluate(a, b, cell, aToB: true);
        var ba = CompareTransferPreview.Evaluate(a, b, cell, aToB: false);

        Assert.True(ab.IsAllowed, ab.BlockReason);
        Assert.True(ba.IsAllowed, ba.BlockReason);
        Assert.Contains("15.25 → 10.25", ab.Description);
        Assert.Contains("10.25 → 15.25", ba.Description);
        Assert.Equal(beforeA, System.IO.File.ReadAllBytes(files.A));
        Assert.Equal(beforeB, System.IO.File.ReadAllBytes(files.B));
        Assert.False(CompareTransferPreview.Evaluate(a, b with { CanWrite = false }, cell, aToB: true).IsAllowed);
        Assert.All(result.Architecture, difference =>
            Assert.False(CompareTransferPreview.Evaluate(a, b, difference, aToB: true).IsAllowed));
    }

    [Fact]
    public async Task MissingObjectCanBeProposedOnlyTowardExistingCompatibleStructure()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await OpenPairAsync(files, includeNewObject: true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var missing = Assert.Single(result.Objects, row => row.Status == CompareStatus.OnlyA && row.Pair?.RecordA is not null);
        var withDependencies = missing with
        {
            Pair = missing.Pair! with
            {
                B = missing.Pair!.B! with { DependencyColumns = new HashSet<string> { "Pressure" } }
            }
        };

        var candidate = CompareTransferPreview.Evaluate(a, b, missing, aToB: true);
        Assert.True(candidate.IsAllowed, candidate.BlockReason);
        Assert.False(CompareTransferPreview.Evaluate(a, b, missing, aToB: false).IsAllowed);
        Assert.False(CompareTransferPreview.Evaluate(a, b, withDependencies, aToB: true).IsAllowed);
    }

    internal static async Task<(CompareSource A, CompareSource B)> OpenPairAsync(
        CompareTestDirectory files, bool includeNewObject = false)
    {
        var first = Sample("Compare A", 10.25m, includeNewObject);
        var second = Sample("Compare B", 15.25m);
        second.Manifest.ProjectId = first.Manifest.ProjectId;
        var service = new ProjectPackageService();
        await service.SaveAsync(files.A, first);
        await service.SaveAsync(files.B, second);
        return (await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: true),
            await CompareSourceReader.OpenOfflineAsync(files.B, allowWrite: true));
    }

    internal static CompareDifference FindDifference(CompareResult result, string column) =>
        Assert.Single(result.Objects.SelectMany(row => row.Children ?? Array.Empty<CompareDifference>()),
            row => row.Column == column && row.Status == CompareStatus.Changed);

    internal static ConfiguratorProject Sample(string name, decimal pressure, bool includeNewObject = false)
    {
        var project = new ConfiguratorProject();
        project.Manifest.Name = name;
        var table = new ProjectTableSchema
        {
            TableName = "dbo.CompareObjects",
            KeyColumns = { "Record" },
            HasStableKey = true,
            Columns =
            {
                new ProjectColumnSchema { Name = "Record", DataType = "int" },
                new ProjectColumnSchema { Name = "Pressure", DataType = "decimal", Precision = 15, Scale = 4 },
                new ProjectColumnSchema { Name = "Neighbour", DataType = "int" }
            }
        };
        project.TableSchemas.Add(table);
        project.LocalRecords.Add(Record(1, pressure));
        if (includeNewObject) project.LocalRecords.Add(Record(2, 20));
        var definition = new ClassDefinition { Id = "compare-test", Name = "Compare object" };
        definition.StorageMappings.Add(new StorageMapping { TableName = table.TableName, KeyColumns = { "Record" } });
        var schema = new DatabaseSchema();
        var physicalTable = new DatabaseTable { Name = "CompareObjects" };
        physicalTable.Keys.Add(new DatabaseKey { Name = "PK_CompareObjects", IsPrimary = true, IsUnique = true, Columns = { "Record" } });
        schema.Tables.Add(physicalTable);
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(schema);
        project.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(new[] { definition });
        return project;
    }

    private static ProjectRecord Record(int key, decimal pressure) => new()
    {
        TableName = "dbo.CompareObjects", Key = key.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Values = new JsonObject { ["Record"] = key, ["Pressure"] = pressure, ["Neighbour"] = 51 }
    };
}
