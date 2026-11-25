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
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OfflinePidCopiesComputedAreaAndPreservesKeyAndLookupGuards(bool forward, bool uniqueName)
    {
        using var files = new CompareTestDirectory();
        var a = PidSnapshot(1, 20, uniqueName);
        var b = PidSnapshot(5, 3, uniqueName);
        b.Manifest.ProjectId = a.Manifest.ProjectId;
        var packages = new ProjectPackageService();
        await packages.SaveAsync(files.A, a);
        await packages.SaveAsync(files.B, b);
        var sourceA = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var sourceB = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)sourceA.Workspace;
        using var workspaceB = (IDisposable)sourceB.Workspace;
        var result = await CompareEngine.CompareAsync(sourceA, sourceB);
        var row = Assert.Single(result.Objects, difference =>
            difference.Status == (forward ? CompareStatus.OnlyA : CompareStatus.OnlyB) &&
            (forward ? difference.Pair?.RecordA : difference.Pair?.RecordB)?.Values["Name"]?.ToString() == "074-PID");
        var preview = CompareTransferPreview.Evaluate(sourceA, sourceB, row, forward);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        Assert.DoesNotContain("Area", preview.Values.Keys);
        if (uniqueName)
        {
            var error = await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyToTargetAsync(preview));
            Assert.Contains("именем", error.Message);
            return;
        }
        await CompareTransfer.ApplyToTargetAsync(preview);
        var saved = await packages.OpenAsync(forward ? files.B : files.A);
        var inserted = Assert.Single(saved.LocalRecords, record =>
            record.TableName == "dbo.Reglers" && record.Values["Name"]?.ToString() == "074-PID" &&
            record.Values["Record"]?.ToString() == (forward ? "20" : "3"));
        Assert.Equal("074", inserted.Values["Area"]?.ToString());
        var journal = new ChangeJournalService();
        journal.BindOfflineProject(saved, () => { }, 3);
        var entry = Assert.IsType<ChangeJournalEntry>(journal.LoadLatestPending());
        Assert.True(entry.CanRestore);
        journal.RestoreOffline(entry, saved, () => { });
        Assert.Single(saved.LocalRecords, record => record.TableName == "dbo.Reglers" && record.Values["Name"]?.ToString() == "074-PID");
    }

    private static ConfiguratorProject PidSnapshot(int plc, int number, bool uniqueName)
    {
        var project = new ConfiguratorProject();
        var physical = new DatabaseSchema();
        void Table(string name, string[] keys, JsonObject values, bool includeRecord = true)
        {
            var table = new ProjectTableSchema { TableName = "dbo." + name, HasStableKey = true, KeyColumns = keys.ToList() };
            foreach (var (field, value) in values)
                table.Columns.Add(new ProjectColumnSchema { Name = field,
                    DataType = value?.GetValueKind() == JsonValueKind.String ? "varchar" : "int",
                    IsComputed = field == "Area", IsReadOnly = field == "Area" });
            project.TableSchemas.Add(table);
            var physicalTable = new DatabaseTable { Name = name };
            physicalTable.Keys.Add(new DatabaseKey { IsUnique = true });
            physicalTable.Keys[0].Columns.AddRange(keys);
            if (name == "Reglers" && uniqueName)
                physicalTable.Keys.Add(new DatabaseKey { IsUnique = true, Columns = { "Name" } });
            physical.Tables.Add(physicalTable);
            if (includeRecord)
                project.LocalRecords.Add(new ProjectRecord { TableName = table.TableName, Values = values,
                    Key = ProjectKeyCodec.Encode(table, keys, key => values[key]) });
        }
        Table("PLC", ["Record"], new() { ["Record"] = 1 });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.PLC", Key = "[5]", Values = new() { ["Record"] = 5 } });
        Table("PLC_CFG", ["PLC_Number", "PLC_Class_Number"], new() { ["PLC_Number"] = 1, ["PLC_Class_Number"] = 8 });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.PLC_CFG", Key = "[5,8]",
            Values = new() { ["PLC_Number"] = 5, ["PLC_Class_Number"] = 8 } });
        Table("Reglers_Types", ["Record"], new() { ["Record"] = 1 }, includeRecord: false);
        Table("Areas", ["AreaName"], new() { ["AreaName"] = "074" });
        Table("Reglers", ["PLC", "Record", "PLC_Class_Number"], new()
        {
            ["PLC"] = plc, ["Record"] = number, ["PLC_Class_Number"] = 8, ["Name"] = "074-PID", ["Area"] = "074", ["Type"] = 1
        });
        project.LocalRecords.Add(new ProjectRecord { TableName = "dbo.Reglers", Key = "[7,3,8]", Values = new()
        {
            ["PLC"] = 7, ["Record"] = 3, ["PLC_Class_Number"] = 8, ["Name"] = "074-Other", ["Area"] = "074", ["Type"] = 1
        } });
        physical.Relations.Add(new DatabaseRelation { SourceTable = "dbo.Reglers", TargetTable = "dbo.Reglers_Types",
            Kind = DatabaseRelationKind.ConventionalLookup, SourceColumns = { "Type" }, TargetColumns = { "Record" } });
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(physical);
        project.Architecture["classDefinitions"] = JsonSerializer.SerializeToNode(new[]
        {
            new ClassDefinition { Id = "pid", Name = "PID", IsSystem = true, ClassNumber = 8, Available = true }
        });
        return project;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditedFieldPersistsAndCannotOverwriteAConcurrentEdit(bool aToB)
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await CompareTransferPreviewTests.OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var difference = CompareTransferPreviewTests.FindDifference(await CompareEngine.CompareAsync(a, b), "Pressure");
        var original = CompareTransferPreview.Evaluate(a, b, difference, aToB);
        Assert.True(original.IsAllowed, original.BlockReason);
        foreach (var invalid in new string?[] { "1.23456", "abc", null })
            Assert.Throws<InvalidOperationException>(() => original.WithEdits(new Dictionary<string, string?> { ["Pressure"] = invalid }));
        Assert.Throws<InvalidOperationException>(() => original.WithEdits(new Dictionary<string, string?> { ["Record"] = "2" }));
        var edited = original.WithEdits(new Dictionary<string, string?> { ["Pressure"] = "24,75" });
        var target = aToB ? files.B : files.A;
        var source = aToB ? files.A : files.B;
        var beforeSource = File.ReadAllBytes(source);
        await CompareTransfer.ApplyToTargetAsync(edited);
        var record = Assert.Single((await new ProjectPackageService().OpenAsync(target)).LocalRecords);
        Assert.Equal(24.75m, record.Values["Pressure"]!.GetValue<decimal>());
        Assert.Equal(51, record.Values["Neighbour"]!.GetValue<int>());
        var saved = File.ReadAllBytes(target);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyToTargetAsync(original));
        Assert.Equal(saved, File.ReadAllBytes(target));
        Assert.Equal(beforeSource, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task OccupiedRecordCanBeRetriedWithNewNumber()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await CompareTransferPreviewTests.OpenPairAsync(files, includeNewObject: true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var missing = Assert.Single(result.Objects, row => row.Status == CompareStatus.OnlyA && row.Pair?.RecordA is not null);
        var candidate = CompareTransferPreview.Evaluate(a, b, missing, aToB: true);
        var occupied = candidate.WithEdits(new Dictionary<string, string?> { ["Record"] = "1" });
        await Assert.ThrowsAsync<CompareRecordConflictException>(() => CompareTransfer.ApplyToTargetAsync(occupied));
        var retry = occupied.WithEdits(new Dictionary<string, string?> { ["Record"] = "42" });
        await CompareTransfer.ApplyToTargetAsync(retry);
        var saved = await new ProjectPackageService().OpenAsync(files.B);
        Assert.Contains(saved.LocalRecords, row => row.Values["Record"]?.ToString() == "42");
    }

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
            var journalNode = Assert.Single(Assert.IsType<JsonArray>(copied.Architecture["changeJournal"]));
            var journalEntry = JsonSerializer.Deserialize<ChangeJournalEntry>(journalNode!.ToJsonString());
            Assert.NotNull(journalEntry);
            Assert.Equal(CompareProjectSettingsRestore.RestoreKind, journalEntry!.RestoreKind);
            Assert.True(journalEntry.CanRestore);
            journalEntry.ProjectType = "Online";
            journalEntry.Server = "test-server";
            journalEntry.Database = "test-database";
            string variant = transferTheme ? "theme" : "list";
            var store = new ProjectCompareSettingsStore(System.IO.Path.Combine(files.Path, $"online-settings-{variant}.json"));
            var emptySettings = new ProjectCompareSettings([], []);
            var copiedSettings = new ProjectCompareSettings(copied.TextLists, copied.ThemePresets);
            await store.SaveAsync(copiedSettings, CompareMetadataTransferPreview.FingerprintProjectSettings(emptySettings));
            await CompareProjectSettingsRestore.RestoreOnlineAsync(journalEntry, store);
            var onlineRestored = await store.LoadAsync();
            Assert.Empty(onlineRestored.TextLists);
            Assert.Empty(onlineRestored.ThemePresets);

            var staleStore = new ProjectCompareSettingsStore(System.IO.Path.Combine(files.Path, $"stale-online-settings-{variant}.json"));
            await staleStore.SaveAsync(copiedSettings, CompareMetadataTransferPreview.FingerprintProjectSettings(emptySettings));
            var storedCopiedSettings = await staleStore.LoadAsync();
            var concurrentSettings = new ProjectCompareSettings(copiedSettings.TextLists,
                copiedSettings.ThemePresets.Append(new ThemePreset { Name = "Concurrent" }).ToList());
            await staleStore.SaveAsync(concurrentSettings, CompareMetadataTransferPreview.FingerprintProjectSettings(storedCopiedSettings));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CompareProjectSettingsRestore.RestoreOnlineAsync(journalEntry, staleStore));

            journalEntry.ProjectType = "Offline";
            journalEntry.ProjectId = copied.Manifest.ProjectId;
            if (transferTheme)
                Assert.Equal(System.Windows.Media.Color.FromRgb(22, 80, 160), Assert.Single(copied.ThemePresets).Accent);
            else
                Assert.Equal("Ball valve", Assert.Single(Assert.Single(copied.TextLists).Items).Text);

            CompareProjectSettingsRestore.RestoreOffline(journalEntry, copied);
            Assert.Empty(copied.TextLists);
            Assert.Empty(copied.ThemePresets);
            Assert.Same(appTheme, ThemeManager.CurrentPreset);
        }
    }

    [Fact]
    public async Task OfflineProjectSettingsRestoreRejectsAnyPostTransferSettingsEdit()
    {
        using var files = new CompareTestDirectory();
        var sourceProject = CompareTransferPreviewTests.Sample("Compare A", 10.25m);
        sourceProject.TextLists.Add(new ProjectTextList
        {
            Name = "Valve types", SourceTable = "dbo.ValveTypes", KeyColumn = "Type", DisplayColumn = "Name",
            Items = [new ProjectTextListItem { Key = "Ball", Text = "Ball valve" }]
        });
        await new ProjectPackageService().SaveAsync(files.A, sourceProject);
        await new ProjectPackageService().SaveAsync(files.B, CompareTransferPreviewTests.Sample("Compare B", 15.25m));
        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var comparison = await CompareEngine.CompareAsync(a, b);
        var difference = Assert.Single(comparison.Architecture, row => row.MetadataKind == CompareMetadataKind.ProjectTextList);
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB: true);
        await CompareTransfer.ApplyMetadataToTargetAsync(preview);

        var changed = await new ProjectPackageService().OpenAsync(files.B);
        changed.ThemePresets.Add(new ThemePreset { Name = "Concurrent theme" });
        var entryNode = Assert.Single(Assert.IsType<JsonArray>(changed.Architecture["changeJournal"]));
        var entry = JsonSerializer.Deserialize<ChangeJournalEntry>(entryNode!.ToJsonString());
        Assert.NotNull(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            CompareProjectSettingsRestore.RestoreOffline(entry!, changed)));
        Assert.Equal("Concurrent theme", Assert.Single(changed.ThemePresets).Name);
        Assert.Equal("Ball valve", Assert.Single(Assert.Single(changed.TextLists).Items).Text);
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
        var reopened = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var reopenedWorkspace = (IDisposable)reopened.Workspace;
        var staleDifference = await CompareMetadataTransferPreview.EvaluateAsync(a, reopened, difference, aToB: true);
        Assert.False(staleDifference.IsAllowed);
        Assert.Equal(beforeRejectedWrite, File.ReadAllBytes(files.B));
    }

    [Fact]
    public async Task UnknownMetadataTypeIsBlockedBeforeAnyWrite()
    {
        using var files = new CompareTestDirectory();
        var (a, b) = await CompareTransferPreviewTests.OpenPairAsync(files);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var before = await File.ReadAllBytesAsync(files.B);
        var difference = new CompareDifference("unknown", null, null, CompareStatus.Changed,
            MetadataKind: (CompareMetadataKind)12345, MetadataKey: "unknown");
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, true);
        Assert.False(preview.IsAllowed);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyMetadataToTargetAsync(preview));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.B));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineRejectsClassTransferWithoutChangingEitherPackage(bool aToB)
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
        var beforeTarget = File.ReadAllBytes(targetPath);
        var result = await CompareEngine.CompareAsync(a, b);
        var difference = Flatten(result.Architecture).Single(item => item.MetadataKind == CompareMetadataKind.ClassDefinition && item.MetadataKey == "extra");
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB);
        Assert.False(preview.IsAllowed);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyMetadataToTargetAsync(preview));
        Assert.Equal(beforeSource, File.ReadAllBytes(sourcePath));
        Assert.Equal(beforeTarget, File.ReadAllBytes(targetPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineRejectsManualRelationTransfer(bool aToB)
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
        var beforeTarget = File.ReadAllBytes(targetPath);

        var a = await CompareSourceReader.OpenOfflineAsync(files.A, true);
        var b = await CompareSourceReader.OpenOfflineAsync(files.B, true);
        using var workspaceA = (IDisposable)a.Workspace;
        using var workspaceB = (IDisposable)b.Workspace;
        var result = await CompareEngine.CompareAsync(a, b);
        var difference = Flatten(result.Architecture).Single(row => row.MetadataKind == CompareMetadataKind.ManualRelation);
        var preview = await CompareMetadataTransferPreview.EvaluateAsync(a, b, difference, aToB);
        Assert.False(preview.IsAllowed);
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyMetadataToTargetAsync(preview));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
        Assert.Equal(beforeTarget, File.ReadAllBytes(targetPath));
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
        var expectedArchitecture = (JsonObject)bProject.Architecture.DeepClone();
        var actualArchitecture = (JsonObject)afterB.Architecture.DeepClone();
        Assert.Single(Assert.IsType<JsonArray>(actualArchitecture["changeJournal"]));
        expectedArchitecture.Remove("changeJournal");
        actualArchitecture.Remove("changeJournal");
        Assert.True(JsonNode.DeepEquals(expectedArchitecture, actualArchitecture));
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
    public async Task OnlineSourceChangeAfterPreviewRejectsTransferToOffline()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        var (online, offline) = await OpenSqlPairAsync(db, files, 10.25m);
        using var workspaceOnline = (IDisposable)online.Workspace;
        using var workspaceOffline = (IDisposable)offline.Workspace;
        var result = await CompareEngine.CompareAsync(online, offline);
        var preview = CompareTransferPreview.Evaluate(online, offline,
            CompareTransferPreviewTests.FindDifference(result, "Pressure"), aToB: true);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        var before = await File.ReadAllBytesAsync(files.B);
        await db.ExecuteAsync("UPDATE dbo.Valves SET Pressure = 16.25 WHERE Record = 1");
        await Assert.ThrowsAsync<CompareTransferException>(() => CompareTransfer.ApplyToTargetAsync(preview));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.B));
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
        var preview = CompareTransferPreview.Evaluate(online, offline, CompareTransferPreviewTests.FindDifference(result, "Pressure"), aToB: false);
        await CompareTransfer.ApplyToTargetAsync(preview.WithEdits(new Dictionary<string, string?> { ["Pressure"] = "12.75" }));
        Assert.Equal(12.75m, await db.DecimalAsync("SELECT Pressure FROM dbo.Valves WHERE Record=1"));
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
    public async Task OnlineCompareUpdateJournalsActualDefaultsKeysAndComputedValuesForRestore()
    {
        using var files = new CompareTestDirectory();
        await using var db = await CompareLocalDb.CreateAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Valves (Record int IDENTITY(1,1) NOT NULL PRIMARY KEY, Value int NOT NULL,
                Defaulted int NOT NULL CONSTRAINT DF_CompareJournal_Defaulted DEFAULT (17),
                ComputedValue AS (Value * 2) PERSISTED);
            INSERT dbo.Valves (Value) VALUES (4);
            """);
        var loaded = await CompareSourceReader.OpenOnlineAsync(db.ConnectionString, "Compare journal", allowWrite: true);
        using var loadedWorkspace = (IDisposable)loaded.Workspace;
        var reader = new OnlineProjectDataSource(db.ConnectionString, "Compare journal");
        var online = loaded with
        {
            Workspace = new ProjectWorkspace(reader, new OnlineProjectCrudDataSource(db.ConnectionString),
                new ProjectArchitectureDataSource(new ConfiguratorProject(), strictReads: true))
        };
        var tables = (await online.Workspace.GetTablesAsync()).ToList();
        var rows = (await online.Workspace.ReadPageAsync(new ProjectReadQuery { TableName = "dbo.Valves" })).Items.ToList();
        rows[0].Values["Value"] = 12;
        var project = new ConfiguratorProject { TableSchemas = tables, LocalRecords = rows };
        var identity = new SqlConnectionStringBuilder(db.ConnectionString);
        project.Architecture["sourceServer"] = identity.DataSource;
        project.Architecture["sourceDatabase"] = identity.InitialCatalog;
        project.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(online.PhysicalSchema);
        await new ProjectPackageService().SaveAsync(files.B, project);
        var offline = await CompareSourceReader.OpenOfflineAsync(files.B, allowWrite: true);
        using var workspaceOffline = (IDisposable)offline.Workspace;

        var result = await CompareEngine.CompareAsync(online, offline);
        var difference = CompareTransferPreviewTests.FindDifference(result, "Value");
        var preview = CompareTransferPreview.Evaluate(online, offline, difference, aToB: false);
        Assert.True(preview.IsAllowed, preview.BlockReason);
        var captured = await ((ProjectWorkspace)online.Workspace).UpdateCompareRecordAsync(preview.TargetSchema!, preview.ExpectedRecord,
            preview.KeyValues, preview.Values);
        var entry = CompareTransfer.CreateRowJournalEntry(preview, captured);

        var refreshed = await CompareEngine.RefreshObjectAsync(difference.Pair!, online, preview, aToB: false);
        Assert.NotNull(refreshed);
        Assert.Contains(refreshed.Children!, field => field.Column == "ComputedValue" && field.Status == CompareStatus.Changed);
        Assert.Contains(refreshed.Children!, field => field.Column == "Value" && field.Status == CompareStatus.Equal);

        Assert.True(entry.RestoreIncludesAllChanges);
        Assert.True(entry.CanRestore);
        Assert.Equal(1, captured.IdentityAfter["Record"].ToObject());
        Assert.Equal(1, captured.Before["Record"].ToObject());
        Assert.Equal(17, captured.Before["Defaulted"].ToObject());
        Assert.Equal(17, captured.After["Defaulted"].ToObject());
        Assert.Equal(8, captured.Before["ComputedValue"].ToObject());
        Assert.Equal(24, captured.After["ComputedValue"].ToObject());

        await new ChangeJournalService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            .RestoreAsync(entry, db.ConnectionString);
        Assert.Equal(4, await db.DecimalAsync("SELECT Value FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(17, await db.DecimalAsync("SELECT Defaulted FROM dbo.Valves WHERE Record=1"));
        Assert.Equal(8, await db.DecimalAsync("SELECT ComputedValue FROM dbo.Valves WHERE Record=1"));
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
        preview = preview.WithEdits(new Dictionary<string, string?> { ["Pressure"] = "22.5" });
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
