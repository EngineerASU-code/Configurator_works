#nullable enable
using System.IO;
using System.Text.Json;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class CompareSourceReaderTests
{
    [Fact]
    public async Task OpeningOfflineForComparePreservesBothPackagesAndUsesCapturedMetadataUnderSqlGuard()
    {
        using var files = new CompareTestDirectory();
        var service = new ProjectPackageService();
        var first = new ConfiguratorProject();
        first.Manifest.Name = "Compare A";
        first.Architecture["sourceServer"] = "test-server";
        first.Architecture["sourceDatabase"] = "test-only";
        var schema = new DatabaseSchema();
        var table = new DatabaseTable { Name = "Objects" };
        table.Columns.Add(new DatabaseColumn { Name = "Value", DataType = "int", DefaultDefinition = "((5))" });
        schema.Tables.Add(table);
        first.Architecture["physicalSchema"] = JsonSerializer.SerializeToNode(schema);
        var second = new ConfiguratorProject();
        second.Manifest.Name = "Compare B";
        await service.SaveAsync(files.A, first);
        await service.SaveAsync(files.B, second);
        var beforeA = File.ReadAllBytes(files.A);
        var beforeB = File.ReadAllBytes(files.B);

        var previousGuard = SqlProviderGuard.IsOfflineActive;
        SqlProviderGuard.SetOfflineActive(true);
        try
        {
            var a = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: true);
            using var workspaceA = (IDisposable)a.Workspace;
            var b = await CompareSourceReader.OpenOfflineAsync(files.B, allowWrite: false);
            using var workspaceB = (IDisposable)b.Workspace;
            Assert.True(a.Workspace.IsOffline);
            Assert.Equal("Compare A", a.Workspace.DisplayName);
            Assert.True(a.CanWrite);
            Assert.False(b.CanWrite);
            Assert.Equal("test-server\0test-only", a.Origin);
            Assert.Equal("((5))", Assert.Single(Assert.Single(a.PhysicalSchema!.Tables).Columns).DefaultDefinition);
            Assert.NotNull(b.MetadataIssue);
            Assert.Equal(beforeA, File.ReadAllBytes(files.A));
            Assert.Equal(beforeB, File.ReadAllBytes(files.B));
        }
        finally
        {
            SqlProviderGuard.SetOfflineActive(previousGuard);
        }
    }

    [Fact]
    public async Task AlreadyOpenOfflineProjectCanBeComparedButCannotBeATarget()
    {
        using var files = new CompareTestDirectory();
        await new ProjectPackageService().SaveAsync(files.A, new ConfiguratorProject());
        var before = File.ReadAllBytes(files.A);
        using (File.Open(files.A, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var source = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: true);
            using var workspace = (IDisposable)source.Workspace;
            Assert.False(source.CanWrite);
            Assert.True(source.Workspace.IsReadOnly);
        }
        Assert.Equal(before, File.ReadAllBytes(files.A));
    }

    [Fact]
    public async Task PendingRecoveryIsReportedWithoutModifyingPackageOrRecoveryFiles()
    {
        using var files = new CompareTestDirectory();
        var service = new ProjectPackageService();
        await service.SaveAsync(files.A, new ConfiguratorProject());
        var marker = files.A + ".save-in-progress";
        var backup = files.A + ".recovery-backup";
        File.WriteAllText(marker, "interrupted test save");
        File.WriteAllText(backup, "saved test backup");
        var original = File.ReadAllBytes(files.A);

        await Assert.ThrowsAsync<InvalidDataException>(() => CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false));

        Assert.Equal(original, File.ReadAllBytes(files.A));
        Assert.Equal("interrupted test save", File.ReadAllText(marker));
        Assert.Equal("saved test backup", File.ReadAllText(backup));
    }

    [Fact]
    public async Task InvalidSavedArchitectureIsReportedAsAReadError()
    {
        using var files = new CompareTestDirectory();
        var project = new ConfiguratorProject();
        project.Architecture["classDefinitions"] = new System.Text.Json.Nodes.JsonArray("invalid definition");
        await new ProjectPackageService().SaveAsync(files.A, project);
        var a = await CompareSourceReader.OpenOfflineAsync(files.A, allowWrite: false);
        using var workspace = (IDisposable)a.Workspace;
        await Assert.ThrowsAsync<JsonException>(() => a.Workspace.GetClassDefinitionsAsync());
    }
}

internal sealed class CompareTestDirectory : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("gass-compare-test-").FullName;
    internal string A => System.IO.Path.Combine(Path, "A.cfgproj");
    internal string B => System.IO.Path.Combine(Path, "B.cfgproj");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
