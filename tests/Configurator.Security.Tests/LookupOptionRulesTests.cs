using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class LookupOptionRulesTests
{
    [Fact]
    public void ReglerLegacyDictionaryResolvesInEitherDirection()
    {
        foreach (var (available, requested) in new[] { ("dbo.ReglerType", "dbo.Reglers_Types"), ("dbo.Reglers_Types", "dbo.ReglerType") })
            Assert.Equal(available, LookupOptionRules.ResolveLegacyTable([available], "dbo.Reglers", "PID", "Type", requested, true, true));
    }

    [Fact]
    public void ResolveKeyField_PrefersConfiguredAndRecognizedIdsWithoutUsingLabelsAsKeys()
    {
        var columns = new[] { "Name", "Record", "RecordID" };

        Assert.Equal("RecordID", LookupOptionRules.ResolveKeyField(columns, null, "RecordID"));
        Assert.Equal("Record", LookupOptionRules.ResolveKeyField(columns, null, null));
        Assert.Null(LookupOptionRules.ResolveKeyField(new[] { "Name", "Description" }, null, null));
        Assert.Null(LookupOptionRules.ResolveKeyField(new[] { "DeviceId", "AreaId" }, null, null));
    }

    [Fact]
    public void ResolveKeyField_UsesDeclaredPrimaryKeyWhenNoKnownIdColumnExists()
    {
        Assert.Equal("Code", LookupOptionRules.ResolveKeyField(
            new[] { "Code", "Caption" }, new[] { "Code" }, null));
    }

    [Fact]
    public void KeepUnambiguousKeys_CollapsesIdenticalRowsAndDropsConflictingOrCaseAmbiguousKeys()
    {
        var options = new List<LookupOption>
        {
            new() { Key = "1", Display = "One" },
            new() { Key = "1", Display = "One" },
            new() { Key = "2", Display = "Two A" },
            new() { Key = "2", Display = "Two B" },
            new() { Key = "abc", Display = "Text" },
            new() { Key = "ABC", Display = "Text" }
        };

        var result = LookupOptionRules.KeepUnambiguousKeys(options);

        var option = Assert.Single(result);
        Assert.Equal("1", option.Key);
        Assert.Equal("One", option.Display);
    }

    [Fact]
    public void OfflineLookup_DoesNotInventKeysOrChooseBetweenConflictingLabels()
    {
        var project = new ConfiguratorProject();
        project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = "dbo.Lookup",
            Columns = new[] { "Name", "Description" }
                .Select(name => new ProjectColumnSchema { Name = name }).ToList()
        });
        AddRecord(project, "dbo.Lookup", ("Name", "Label only"), ("Description", "No ID"));

        Assert.Empty(OfflineLookupOptionsService.Get(project, "Any", "dbo.Source",
            new FieldDefinition { Name = "Custom", LookupTable = "dbo.Lookup", LookupKeyField = "Record" }));

        project.TableSchemas[0].Columns.Add(new ProjectColumnSchema { Name = "Record" });
        AddRecord(project, "dbo.Lookup", ("Record", 7), ("Name", "First"));
        AddRecord(project, "dbo.Lookup", ("Record", 7), ("Name", "Second"));

        Assert.Empty(OfflineLookupOptionsService.Get(project, "Any", "dbo.Source",
            new FieldDefinition { Name = "Custom", LookupTable = "dbo.Lookup", LookupKeyField = "Record" }));
    }

    private static void AddRecord(ConfiguratorProject project, string table, params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values) json[name] = JsonValue.Create(value);
        project.LocalRecords.Add(new ProjectRecord { TableName = table, Values = json });
    }
}
