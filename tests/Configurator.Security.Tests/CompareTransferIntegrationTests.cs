#nullable enable
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator.Core.Compare;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Configurator.Themes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class CompareTransferIntegrationTests
{
    [Fact]
    public async Task OfflineTransfersProjectTextListsAndThemePresetsWithoutApplyingThemToTheApp()
    {
        using var files = new CompareTestDirectory();
        var appTheme = ThemeManager.CurrentPreset;
        foreach (var transferTheme in new[] { false, true })
        {
            var projectA = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
            var projectB = CompareTransferPreviewTests.Sample("Compare B", 15.25m);
            if (transferTheme)
                projectA.ThemePresets.Add(new ThemePreset { Name = "Project blue", Accent = System.Windows.Media.Color.FromRgb(22, 80, 160) });
            else
                projectA.TextLists.Add(new ProjectTextList
                {
                    Name = "Valve types", SourceTable = "dbo.ValveTypes", KeyColumn = "Type", DisplayColumn = "Name",
                    Items = [new ProjectTextListItem { Key = "Ball", Text = "Ball valve" }]
                });
            await new ProjectPackageService().SaveAsync(files.A, projectA);
            await new ProjectPackageService().SaveAsync(files.B, projectB);
            var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
            var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
            using var workspaceA = (IDisposable)a.Workspace;
            using var workspaceB = (IDisposable)b.Workspace;
            var result = await CompareEngine.CompareAsync(a, b);

            var kind = transferTheme ? CompareMetadataKind.ThemePreset : CompareMetadataKind.ProjectTextList;
            var difference = Assert.Single(result.Architecture, item => item.MetadataKind == kind);
            Assert.Equal(CompareStatus.OnlyA, difference.Status);
            var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB: true);
            Assert.True(preview.IsAllowed, preview.BlockReason);
            await CompareTransfer.ApplyMetadataToTargetAsync(preview);

            var copied = await new ProjectPackageService().OpenAsync(files.B);
            if (transferTheme)
                Assert.Equal(System.Windows.Media.Color.FromRgb(22, 80, 160), Assert.Single(copied.ThemePresets).Accent);
            else
                Assert.Equal("Ball valve", Assert.Single(Assert.Single(copied.TextLists).Items).Text);
            Assert.Same(appTheme, ThemeManager.CurrentPreset);
        }
    }

    [Fact]
    public async Task OfflineProjectSettingsTransferRejectsStaleTargetWithoutOverwritingIt()
    {
        using var files = new CompareTestDirectory();
        var projectA = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
        projectA.TextLists.Add(new ProjectTextList
        {
            Name = "Valve types", SourceTable = "dbo.ValveTypes", KeyColumn = "Type", DisplayColumn = "Type",
            Items = [new ProjectTextListItem { Key = "Ball", Text = "Ball" }]
        });
        await new ProjectPackageService().SaveAsync(files.A, projectA);
        await new ProjectPackageService().SaveAsync(files.B, CompareTransferPreviewTests.Sample("Compare B", 15.25m));
        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var difference = Assert.Single(result.Architecture, item => item.MetadataKind == CompareMetadataKind.ProjectTextList);
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB: true);
        Assert.True(preview.IsAllowed, preview.BlockReason);

        var changedTarget = await new ProjectPackageService().OpenAsync(files.B);
        changedTarget.ThemePresets.Add(new ThemePreset { Name = "Concurrent edit" });
        await new ProjectPackageService().SaveAsync(files.B, changedTarget);
        var beforeRejectedWrite = File.ReadAllBytes(files.B);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyMetadataToTargetAsync(preview));
        Assert.Equal(beforeRejectedWrite, File.ReadAllBytes(files.B));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineTransfersClassSettingsWithoutChangingSource(bool aToB)
    {
        using var files = new CompareTestDirectory();
        var projectA = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
        var projectB = CompareTransferPreviewTests.Sample("Compare B", 15.25m);
        projectB.Manifest.ProjectId = projectA.Manifest.ProjectId;
        var sourceProject = aToB ? projectA : projectB;
        var targetProject = aToB ? projectB : projectA;
        sourceProject.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(new[]
        {
            new Configurator.Core.Architecture.ClassDefinition { Id = "extra", Name = "Extra", Description = "from A" }
        });
        targetProject.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(Array.Empty<Configurator.Core.Architecture.ClassDefinition>());
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, projectA);
        await packages.SaveAsync(files.B, projectB);
        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        string sourcePath = aToB ? files.A : files.B;
        string targetPath = aToB ? files.B : files.A;
        var beforeSource = File.ReadAllBytes(sourcePath);
        var result = await CompareEngine.CompareAsync(a, b);
        var difference = Flatten(result.Architecture).Single(item => item.MetadataKind == CompareMetadataKind.ClassDefinition && item.MetadataKey == "extra");
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        await CompareTransfer.ApplyMetadataToTargetAsync(preview);
        var restored = await packages.OpenAsync(targetPath);
        var definitions = JsonSerializer.Deserialize<Configurator.Core.Architecture.ClassDefinition[]>(restored.Architecture["classDefinitions"]!.ToJsonString());
        Assert.Equal("from A", Assert.Single(definitions!).Description);
        Assert.Equal(beforeSource, File.ReadAllBytes(sourcePath));

        var afterTarget = File.ReadAllBytes(targetPath);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyMetadataToTargetAsync(preview));
        Assert.Equal(afterTarget, File.ReadAllBytes(targetPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineTransfersManualRelationAndLeavesSourceUntouched(bool aToB)
    {
        using var files = new CompareTestDirectory();
        var projectA = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
        var projectB = CompareTransferPreviewTests.Sample("Compare B", 10.25m);
        var source = aToB ? projectA : projectB;
        var target = aToB ? projectB : projectA;
        target.Manifest.ProjectId = source.Manifest.ProjectId;
        source.GraphRelations = JsonSerializer.SerializeToNode(new GraphRelationProjectDocument
        {
            ManualRelations = [new GraphRelationDefinition
            {
                Source = "dbo.CompareObjects", SourceColumn = "Neighbour", Target = "dbo.CompareObjects",
                TargetColumn = "Record", Rule = "Explicit neighbour mapping"
            }]
        })!.AsObject();
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, projectA);
        await packages.SaveAsync(files.B, projectB);
        string sourcePath = aToB ? files.A : files.B;
        string targetPath = aToB ? files.B : files.A;
        var before = File.ReadAllBytes(sourcePath);

        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var difference = Flatten(result.Architecture).Single(row => row.MetadataKind == CompareMetadataKind.ManualRelation);
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        await CompareTransfer.ApplyMetadataToTargetAsync(preview);

        var reopened = await packages.OpenAsync(targetPath);
        var relation = Assert.Single(reopened.GraphRelations.Deserialize<GraphRelationProjectDocument>()!.ManualRelations);
        Assert.Equal(("Neighbour", "Record", "Explicit neighbour mapping"), (relation.SourceColumn, relation.TargetColumn, relation.Rule));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public async Task OfflineTransfersUpdateOnlyTheChosenValueAndPersistInBothDirections()
    {
        using var files = new CompareTestDirectory();
        var (a, originalB) = await CompareTransferPreviewTests.OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var originalWorkspaceB = (IDisposable)originalB.Workspace;
        var packages = new ProjectPackageService();
        var bProject = await packages.OpenAsync(files.B);
        bProject.LocalRecords[0].Values["Neighbour"] = 72;
        await packages.SaveAsync(files.B, bProject);

        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceB = (IDisposable)b.Workspace;
        byte[] initialA = File.ReadAllBytes(files.A);
        var preTransfer = await CompareEngine.CompareAsync(a, b);
        await ApplyAsync(a, b, CompareTransferPreviewTests.FindDifference(preTransfer, "Pressure"), aToB: true);
        var afterB = await packages.OpenAsync(files.B);
        Assert.Equal(10.25m, afterB.LocalRecords[0].Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(72, afterB.LocalRecords[0].Values["Neighbour"]!.GetValue<int>());
        Assert.Equal(initialA, File.ReadAllBytes(files.A));

        var savedB = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var savedWorkspace = (IDisposable)savedB.Workspace;
        byte[] savedBytes = File.ReadAllBytes(files.B);
        var afterTransfer = await CompareEngine.CompareAsync(a, savedB);
        await ApplyAsync(a, savedB, CompareTransferPreviewTests.FindDifference(afterTransfer, "Neighbour"), aToB: false);
        var afterA = await packages.OpenAsync(files.A);
        Assert.Equal(72, afterA.LocalRecords[0].Values["Neighbour"]!.GetValue<int>());
        Assert.Equal(10.25m, afterA.LocalRecords[0].Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(savedBytes, File.ReadAllBytes(files.B));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(bProject.Architecture, afterB.Architecture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineObjectTransferPersistsAllChangedFieldsInEitherDirection(bool aToB)
    {
        using var files = new CompareTestDirectory();
        var packages = new ProjectPackageService();
        var first = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
        var second = CompareTransferPreviewTests.Sample("Compare B", 15.25m);
        second.Manifest.ProjectId = first.Manifest.ProjectId;
        second.LocalRecords[0].Values["Neighbour"] = 72;
        await packages.SaveAsync(files.A, first);
        await packages.SaveAsync(files.B, second);
        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var sourcePath = aToB ? files.A : files.B;
        var targetPath = aToB ? files.B : files.A;
        var originalSource = File.ReadAllBytes(sourcePath);
        var comparison = await CompareEngine.CompareAsync(a, b);
        var difference = Assert.Single(comparison.Objects, item => item.Status == CompareStatus.Changed && item.Pair is not null);

        await ApplyAsync(a, b, difference, aToB);

        var record = Assert.Single((await packages.OpenAsync(targetPath)).LocalRecords);
        Assert.Equal(aToB ? 10.25m : 15.25m, record.Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(aToB ? 51 : 72, record.Values["Neighbour"]!.GetValue<int>());
        Assert.Equal(originalSource, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public async Task OfflineCanCreateSimpleObjectWithoutChangingItsSource()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await CompareTransferPreviewTests.OpenPairAsync(files, includeNewObject: true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var beforeA = File.ReadAllBytes(files.A);
        var result = await CompareEngine.CompareAsync(a, b);
        var added = Assert.Single(result.Objects, x => x.Status == CompareStatus.OnlyA && x.Pair is not null);

        await ApplyAsync(a, b, added, aToB: true);

        var records = (await new ProjectPackageService().OpenAsync(files.B)).LocalRecords;
        Assert.Equal(2, records.Count);
        Assert.Equal(20m, Assert.Single(records, row => row.Values["Record"]!.GetValue<int>() == 2)
            .Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(beforeA, File.ReadAllBytes(files.A));
    }

    [Fact]
    public async Task OfflineCanFillOneMissingParameterInAnExistingRecord()
    {
        using var files = new CompareTestDirectory();
        var (a, previousB) = await CompareTransferPreviewTests.OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var previousWorkspaceB = (IDisposable)previousB.Workspace;
        var packages = new ProjectPackageService();
        var package = await packages.OpenAsync(files.B);
        package.LocalRecords[0].Values.Remove("Neighbour");
        await packages.SaveAsync(files.B, package);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceB = (IDisposable)b.Workspace;

        var result = await CompareEngine.CompareAsync(a, b);
        var missing = Assert.Single(result.Objects.SelectMany(row => row.Children ?? []),
            row => row.Column == "Neighbour" && row.Status == CompareStatus.OnlyA);
        await ApplyAsync(a, b, missing, aToB: true);

        var row = Assert.Single((await packages.OpenAsync(files.B)).LocalRecords);
        Assert.Equal(51, row.Values["Neighbour"]!.GetValue<int>());
        Assert.Equal(15.25m, row.Values["Pressure"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task OfflineRejectsStaleAndLockedPackageWithoutWritingIt()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await CompareTransferPreviewTests.OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var cell = CompareTransferPreviewTests.FindDifference(result, "Pressure");
        var preview = CompareTransferPreview.Evaluate(a, b, cell, aToB: true);
        Assert.True(preview.IsAllowed, preview.BlockReason);

        using (File.Open(files.B, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<Exception>(() => CompareTransfer.ApplyToTargetAsync(preview));

        var packages = new ProjectPackageService();
        var newer = await packages.OpenAsync(files.B);
        newer.LocalRecords[0].Values["Neighbour"] = 88;
        await packages.SaveAsync(files.B, newer);
        var afterExternalSave = File.ReadAllBytes(files.B);
        await Assert.ThrowsAnyAsync<Exception>(() => CompareTransfer.ApplyToTargetAsync(preview));
        Assert.Equal(afterExternalSave, File.ReadAllBytes(files.B));
    }

    [CompareLocalDbFact]
    public async Task OnlineAndOfflineCanEachBeTheDestination()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        var (online, offline) = await OpenSqlPairAsync(db, files, 10.25m);
        using var workspaceOnline = (IDisposable)online.Workspace;
        using var workspaceOffline = (IDisposable)offline.Workspace;
        var beforePackage = File.ReadAllBytes(files.B);
        var result = await CompareEngine.CompareAsync(online, offline);
        await ApplyAsync(online, offline, CompareTransferPreviewTests.FindDifference(result, "Pressure"), aToB: false);
        Assert.Equal(10.25m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(51m, await db.DecimalAsync("SELECT Neighbour FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(beforePackage, File.ReadAllBytes(files.B));
        var next = await CompareEngine.CompareAsync(online, offline);
        await ApplyAsync(online, offline, CompareTransferPreviewTests.FindDifference(next, "Neighbour"), aToB: true);
        var output = await new ProjectPackageService().OpenAsync(files.B);
        Assert.Equal(51, output.LocalRecords[0].Values["Neighbour"]!.GetValue<int>());
        Assert.Equal(10.25m, output.LocalRecords[0].Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(4m, await db.DecimalAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Valves')"));
    }

    [CompareLocalDbFact]
    public async Task OnlineObjectTransferWritesAllChangedFieldsAtomically()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        var (online, offline) = await OpenSqlPairAsync(db, files, 10.25m);
        using var workspaceOnline = (IDisposable)online.Workspace;
        using var workspaceOffline = (IDisposable)offline.Workspace;
        var beforePackage = File.ReadAllBytes(files.B);
        var result = await CompareEngine.CompareAsync(online, offline);
        var difference = Assert.Single(result.Objects, item => item.Status == CompareStatus.Changed && item.Pair is not null);

        await ApplyAsync(online, offline, difference, aToB: false);

        Assert.Equal(10.25m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(72m, await db.DecimalAsync("SELECT Neighbour FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(beforePackage, File.ReadAllBytes(files.B));
    }

    [CompareLocalDbFact]
    public async Task OnlineRejectsCaseAndPaddingChangesAfterCompare()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        var (online, offline) = await OpenSqlPairAsync(db, files, 10.25m);
        using var workspaceOnline = (IDisposable)online.Workspace;
        using var workspaceOffline = (IDisposable)offline.Workspace;
        var result = await CompareEngine.CompareAsync(online, offline);
        var preview = CompareTransferPreview.Evaluate(online, offline,
            CompareTransferPreviewTests.FindDifference(result, "Pressure"), aToB: false);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        await db.ExecuteAsync("UPDATE dbo.Valves SET Label = N'Tag ' WHERE Record = 1");

        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyToTargetAsync(preview));
        Assert.Equal(15.25m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(8m, await db.DecimalAsync("SELECT DATALENGTH(Label) FROM dbo.Valves WHERE Record=1"));
    }

    [CompareLocalDbFact]
    public async Task SqlConstraintFailureLeavesBothProjectsIntact()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        var (online, offline) = await OpenSqlPairAsync(db, files, 99m);
        using var workspaceOnline = (IDisposable)online.Workspace;
        using var workspaceOffline = (IDisposable)offline.Workspace;
        var result = await CompareEngine.CompareAsync(online, offline);
        var expectedSnapshot = File.ReadAllBytes(files.B);
        var preview = CompareTransferPreview.Evaluate(online, offline,
            CompareTransferPreviewTests.FindDifference(result, "Pressure"), aToB: false);
        Assert.True(preview.IsAllowed, preview.BlockReason);

        var error = await Assert.ThrowsAsync<SqlException>(() => CompareTransfer.ApplyToTargetAsync(preview));
        Assert.Equal(547, error.Number);
        Assert.Equal(15.25m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(51m, await db.DecimalAsync("SELECT Neighbour FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(expectedSnapshot, File.ReadAllBytes(files.B));
    }

    private static async Task<(CompareSource Online, CompareSource Offline)> OpenSqlPairAsync(
        CompareLocalDb db, CompareTestDirectory files, decimal offlinePressure)
    {
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valves (Record int NOT NULL PRIMARY KEY, Pressure decimal(15,4) NOT NULL,
                Neighbour int NOT NULL, Label nvarchar(30) NOT NULL, CHECK (Pressure <= 20));
            INSERT dbo.Valves VALUES (1, 15.25, 51, N'tag');
            """);
        var loaded = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "Compare SQL", true);
        using var loadedWorkspace = (IDisposable)loaded.Workspace;
        var reader = new OnlineProjectDataSource(db.ConnectionString, "Compare SQL");
        var online = loaded with
        {
            Workspace = new ProjectWorkspace(reader, new OnlineProjectCrudDataSource(db.ConnectionString),
                new ProjectArchitectureDataSource(new ConfiguratorProject(), strictReads: true))
        };
        Assert.True(online.PhysicalSchema?.FindTable("dbo.Valves")?.CanCompareUpdate == true, online.MetadataIssue);
        var tables = (await online.Workspace.GetTablesAsync()).ToList();
        var rows = (await online.Workspace.ReadPageAsync(new ProjectReadQuery { TableName = "dbo.Valves" })).Items.ToList();
        rows[0].Values["Pressure"] = offlinePressure;
        rows[0].Values["Neighbour"] = 72;
        var project = new ConfiguratorProject { TableSchemas = tables, LocalRecords = rows };
        var identity = new SqlConnectionStringBuilder(db.ConnectionString);
        project.Architecture["sourceServer"] = identity.DataSource;
        project.Architecture["sourceDatabase"] = identity.InitialCatalog;
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
        await new ProjectPackageService().SaveAsync(files.B, project);
        return (online, await CompareSourceReader.OpenOfflineAsync(files.B, true));
    }

    private static async Task ApplyAsync(CompareSource a, CompareSource b, CompareDifference difference, bool aToB)
    {
        var preview = CompareTransferPreview.Evaluate(a, b, difference, aToB);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        await CompareTransfer.ApplyToTargetAsync(preview);
    }

    private static IEnumerable<CompareDifference> Flatten(IEnumerable<CompareDifference> rows) =>
        rows.SelectMany(row => new[] { row }.Concat(Flatten(row.Children ?? Array.Empty<CompareDifference>())));
}
