using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ProjectPackageAdversarialTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute/path.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("data\\..\\outside.txt")]
    public async Task PackageReaderDoesNotExtractArchivePaths(string entryName)
    {
        var path = NewPath();
        var outside = Path.Combine(Path.GetTempPath(), "outside.txt");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("payload");
            }
            await Assert.ThrowsAnyAsync<Exception>(() => new ProjectPackageService().OpenAsync(path));
            Assert.False(File.Exists(outside));
        }
        finally { Delete(path); Delete(outside); }
    }

    [Fact]
    public async Task RejectsZipWithDuplicateEntryNames()
    {
        var path = NewPath();
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var _ in new[] { 1, 2 })
                {
                    var entry = archive.CreateEntry("manifest.json");
                    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                    writer.Write("{}");
                }
            }
            await Assert.ThrowsAnyAsync<Exception>(() => new ProjectPackageService().OpenAsync(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RejectsProjectWithNullRequiredCollectionsAndObjects()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.Mappings = null!;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData("dbo.Items;DROP TABLE dbo.Users")]
    [InlineData("dbo.Items/Users")]
    [InlineData("dbo\\Items")]
    [InlineData("a.b.c")]
    public async Task RejectsMalformedTableIdentifiersInMappings(string tableName)
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.Mappings.Add(new ProjectMapping
            {
                EntityName = "Item", TableName = tableName, KeyColumn = "Id", KeyColumns = new() { "Id" }, HasStableKey = true
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task SaveThenReopenPreservesLocalAndBaselineAsIndependentSnapshots()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.LocalRecords.Add(Record("dbo.Items", "local-1", "Local"));
            project.BaselineRecords.Add(Record("dbo.Items", "baseline-1", "Baseline"));
            await new ProjectPackageService().SaveAsync(path, project);
            var reopened = await new ProjectPackageService().OpenAsync(path);
            Assert.Equal("Local", reopened.LocalRecords.Single().Values["Name"]!.GetValue<string>());
            Assert.Equal("Baseline", reopened.BaselineRecords.Single().Values["Name"]!.GetValue<string>());
            Assert.NotEqual(reopened.LocalRecords.Single().Key, reopened.BaselineRecords.Single().Key);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RejectsDuplicateTextListItemKeys()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.TextLists.Add(new ProjectTextList
            {
                Name = "Statuses",
                Items = new() { new ProjectTextListItem { Key = "A", Text = "Active" }, new ProjectTextListItem { Key = "A", Text = "Again" } }
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task FailedValidationNeverLeavesPartialPackageBehind()
    {
        var path = NewPath();
        try
        {
            var project = CreateProject();
            project.LocalRecords.Add(Record("dbo.Items", "same", "one"));
            project.LocalRecords.Add(Record("dbo.Items", "same", "two"));
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectPackageService().SaveAsync(path, project));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp*"));
        }
        finally { Delete(path); }
    }

    private static ConfiguratorProject CreateProject() => new()
    {
        Manifest = new ConfiguratorProjectManifest { Name = "Adversarial test", FormatVersion = 2 },
        Settings = new ProjectSettings { DisplayName = "Adversarial test" },
        Mappings = new(), Architecture = new JsonObject(), GraphRelations = new JsonObject(), Filters = new JsonObject(),
        TextLists = new(), LocalRecords = new(), BaselineRecords = new(), TableSchemas = new()
    };

    private static ProjectRecord Record(string table, string key, string name) => new()
    {
        TableName = table, Key = key, Values = new JsonObject { ["Name"] = name }
    };

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "Configurator-Adversarial-" + Guid.NewGuid().ToString("N") + ".cfgproj");
    private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
