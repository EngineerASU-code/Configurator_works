using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Configurator.Core.Architecture;
using Configurator;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class OfflineLookupOptionsServiceTests
{
    [Fact]
    public void Build_UsesSnapshotLookupsAndPreservesStoredKeys()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type", "PLC", "Class" });
        AddTable(project, "dbo.Valves_Type", new[] { "Record", "Name_L2" }, "Record");
        AddTable(project, "dbo.PLC", new[] { "Record", "Name" }, "Record");
        AddTable(project, "dbo.Classes", new[] { "Record", "Name" }, "Record");
        AddRecord(project, "dbo.Valves_Type", ("Record", 1), ("Name_L2", "Discret"));
        AddRecord(project, "dbo.Valves_Type", ("Record", 2), ("Name_L2", "AS-i"));
        AddRecord(project, "dbo.PLC", ("Record", 6), ("Name", "PLC06"));
        AddRecord(project, "dbo.Classes", ("Record", 7), ("Name", "Digital output"));

        var options = OfflineLookupOptionsService.Build(project, "Valves", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" },
            new ColumnInfo { Name = "PLC", DataType = "int" },
            new ColumnInfo { Name = "Class", DataType = "int" }
        });

        Assert.Equal("1", Assert.Single(options["Type"], item => item.Key == "1").Key);
        Assert.Equal("Discret  [1]", Assert.Single(options["Type"], item => item.Key == "1").Display);
        Assert.Equal("6 · PLC06", Assert.Single(options["PLC"]).Display);
        Assert.Equal("7 · Digital output", Assert.Single(options["Class"]).Display);
    }

    [Fact]
    public void Build_ResolvesLegacyPlcTypeTableAndFormatsAreaLikeOnline()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.PLC", new[] { "Type" });
        AddTable(project, "dbo.PLC_Types", new[] { "Record", "Name" }, "Record");
        AddRecord(project, "dbo.PLC_Types", ("Record", 3), ("Name", "S7-1500"));
        AddTable(project, "dbo.Valves", new[] { "Area" });
        AddTable(project, "dbo.Areas", new[] { "Record", "AreaName", "AreaNameL2" }, "Record");
        AddRecord(project, "dbo.Areas", ("Record", 42), ("AreaName", "42"), ("AreaNameL2", "Цех розлива"));

        var options = OfflineLookupOptionsService.Build(project, "dbo.PLC", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" }
        });
        var areaOptions = OfflineLookupOptionsService.Build(project, "dbo.Valves", new[]
        {
            new ColumnInfo { Name = "Area", DataType = "int" }
        });

        Assert.Equal("3 · S7-1500", Assert.Single(options["Type"]).Display);
        Assert.Equal("42. Цех розлива", Assert.Single(areaOptions["Area"]).Display);
    }

    [Fact]
    public void Build_IncludesConfiguredTextListFieldsOutsideBuiltInLookupNames()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Actions", new[] { "Text1" });
        AddTable(project, "dbo.ActionTexts", new[] { "Record", "Name" }, "Record");
        AddRecord(project, "dbo.ActionTexts", ("Record", 8), ("Name", "Reset valve error"));
        var settings = LookupGenerationDefaults.Create();
        settings.Rules.Add(new LookupGenerationRule
        {
            ClassName = "Actions", SourceTable = "dbo.Actions", SourceField = "Text1",
            LookupTable = "dbo.ActionTexts", KeyField = "Record", DisplayField = "Name", IsUserDefined = true
        });
        LookupGenerationSettingsStore.Save(project, settings);

        var options = OfflineLookupOptionsService.Build(project, "dbo.Actions", new[]
        {
            new ColumnInfo { Name = "Text1", DataType = "int" }
        });

        Assert.Equal("8 · Reset valve error", Assert.Single(options["Text1"]).Display);
    }

    [Fact]
    public void Build_LeavesMissingAndEmptyLookupsUnavailable()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type", "PLC" });
        AddTable(project, "dbo.Valves_Type", new[] { "Record", "Name_L2" }, "Record");
        // Existing schema with no records is still unavailable as a selectable lookup.

        var options = OfflineLookupOptionsService.Build(project, "dbo.Valves", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" },
            new ColumnInfo { Name = "PLC", DataType = "int" }
        });

        Assert.Empty(options);
    }

    [Fact]
    public void Build_UsesEachClassesConfiguredTypeListWithoutMixingKeys()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type" });
        AddTable(project, "dbo.Valve2S", new[] { "Type" });
        AddTable(project, "dbo.Valves_Type", new[] { "Record", "Name_L2" }, "Record");
        AddTable(project, "dbo.Valves2S_Type", new[] { "Record", "Name_L2" }, "Record");
        AddRecord(project, "dbo.Valves_Type", ("Record", 1), ("Name_L2", "Valve type"));
        AddRecord(project, "dbo.Valves2S_Type", ("Record", 2), ("Name_L2", "DSV type"));
        var fields = new[] { new ColumnInfo { Name = "Type", DataType = "int" } };

        var valve = OfflineLookupOptionsService.Build(project, "dbo.Valves", fields);
        var dsv = OfflineLookupOptionsService.Build(project, "dbo.Valve2S", fields);

        Assert.Equal("1", Assert.Single(valve["Type"]).Key);
        Assert.Equal("Valve type  [1]", Assert.Single(valve["Type"]).Display);
        Assert.Equal("2", Assert.Single(dsv["Type"]).Key);
        Assert.Equal("DSV type  [2]", Assert.Single(dsv["Type"]).Display);
    }

    [Fact]
    public void Build_UsesConfiguredKeyAndLabelForArbitraryField()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Actions", new[] { "Status" });
        AddTable(project, "dbo.StatusTexts", new[] { "Code", "Caption" }, "Code");
        AddRecord(project, "dbo.StatusTexts", ("Code", 17), ("Caption", "Ready"));
        var settings = new LookupGenerationSettings();
        settings.Rules.Add(new LookupGenerationRule
        {
            ClassName = "Actions", SourceTable = "dbo.Actions", SourceField = "Status",
            LookupTable = "dbo.StatusTexts", KeyField = "Code", DisplayField = "Caption", IsUserDefined = true
        });
        LookupGenerationSettingsStore.Save(project, settings);

        var options = OfflineLookupOptionsService.Build(project, "dbo.Actions", new[]
        {
            new ColumnInfo { Name = "Status", DataType = "int" }
        });

        Assert.Equal("17", Assert.Single(options["Status"]).Key);
        Assert.Contains("Ready", Assert.Single(options["Status"]).Display);
    }

    [Fact]
    public void Build_DoesNotSubstituteAnotherClassListWhenConfiguredTableIsMissing()
    {
        var project = new ConfiguratorProject();
        AddTable(project, "dbo.Valves", new[] { "Type" });
        AddTable(project, "dbo.Valves2S_Type", new[] { "Record", "Name_L2" }, "Record");
        AddRecord(project, "dbo.Valves2S_Type", ("Record", 2), ("Name_L2", "DSV type"));

        var options = OfflineLookupOptionsService.Build(project, "dbo.Valves", new[]
        {
            new ColumnInfo { Name = "Type", DataType = "int" }
        });

        Assert.Empty(options);
    }

    private static void AddTable(ConfiguratorProject project, string table, IEnumerable<string> columns, params string[] keys)
        => project.TableSchemas.Add(new ProjectTableSchema
        {
            TableName = table,
            KeyColumns = keys.ToList(),
            Columns = columns.Select(name => new ProjectColumnSchema { Name = name, DataType = name == "Name" || name == "Name_L2" ? "varchar" : "int" }).ToList()
        });

    private static void AddRecord(ConfiguratorProject project, string table, params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values) json[name] = JsonValue.Create(value);
        project.LocalRecords.Add(new ProjectRecord { TableName = table, Values = json });
    }
}
