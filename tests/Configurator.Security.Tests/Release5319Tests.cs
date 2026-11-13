using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Configurator;
using Configurator.Core.Architecture;
using Configurator.Infrastructure.Projects;
using Configurator.Core.Projects;
using Xunit;

namespace Configurator.Security.Tests;

/// <summary>Regression tests for the 5.3.19 fixes (journal types, free SQL, lookup rules, Excel names).</summary>
public sealed class Release5319Tests
{
    // ---- change journal: type names coming from files are never instantiated ----------------------------

    [Theory]
    [InlineData(42)]
    [InlineData("text")]
    [InlineData(12.5)]
    public void JournalValue_RoundTripsAllowedPrimitiveTypes(object value)
    {
        var restored = ChangeJournalValue.FromObject(value).ToObject();
        Assert.Equal(Convert.ChangeType(value, value.GetType()), Convert.ChangeType(restored, value.GetType()));
    }

    [Fact]
    public void JournalValue_RoundTripsGuidAndDateTime()
    {
        var id = Guid.NewGuid();
        var when = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc);
        Assert.Equal(id, ChangeJournalValue.FromObject(id).ToObject());
        Assert.Equal(when, ChangeJournalValue.FromObject(when).ToObject());
    }

    [Theory]
    [InlineData("System.Diagnostics.Process, System.Diagnostics.Process")]
    [InlineData("System.Windows.Data.ObjectDataProvider, PresentationFramework")]
    [InlineData("Configurator.UserAccount, Configurator")]
    [InlineData("Does.Not.Exist")]
    public void JournalValue_DoesNotInstantiateArbitraryTypeNames(string typeName)
    {
        var value = new ChangeJournalValue { TypeName = typeName, Json = "{\"Id\":1}" };
        var result = value.ToObject();
        Assert.IsNotType<UserAccount>(result);
        Assert.DoesNotContain("Process", result?.GetType().Name ?? string.Empty);
        Assert.DoesNotContain("ObjectDataProvider", result?.GetType().Name ?? string.Empty);
    }

    [Fact]
    public void JournalValue_ReadsLegacyAssemblyQualifiedNamesOfAllowedTypes()
    {
        var legacy = new ChangeJournalValue
        {
            TypeName = typeof(int).AssemblyQualifiedName ?? "System.Int32",
            Json = "7"
        };
        Assert.Equal(7, legacy.ToObject());
    }

    // ---- free SQL: accidental mass changes and external sources -----------------------------------------

    [Theory]
    [InlineData("UPDATE dbo.Items SET Name = N'x'")]
    [InlineData("DELETE FROM dbo.Items")]
    public void FreeSql_RejectsUpdateAndDeleteWithoutWhere(string sql)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate(sql));
        Assert.Contains("WHERE", ex.Message);
    }

    [Theory]
    [InlineData("UPDATE dbo.Items SET Name = N'x' WHERE 1 = 1")]
    [InlineData("DELETE FROM dbo.Items WHERE Id = 5")]
    public void FreeSql_AllowsExplicitWhere(string sql) => FreeSqlPolicy.Validate(sql);

    [Fact]
    public void FreeSql_RejectsOpenDataSource()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FreeSqlPolicy.Validate("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=remote;').db.dbo.t"));
    }

    // ---- lookup rules -------------------------------------------------------------------------------------

    private static LookupGenerationRule R(string table, string field, bool enabled, string lookup = "dbo.L") => new()
    {
        ClassName = "Valve", SourceTable = table, SourceField = field, Enabled = enabled,
        LookupTable = lookup, KeyField = "Record", DisplayField = "Name"
    };

    [Fact]
    public void Selector_PrefersSpecificEnabledRuleOverDisabledGenericRule()
    {
        var settings = new LookupGenerationSettings();
        settings.Rules.Add(R("", "Type", enabled: false));
        settings.Rules.Add(R("dbo.Valves", "Type", enabled: true, lookup: "dbo.Specific"));

        var rule = LookupGenerationRuleSelector.Find(settings, "Valve", "dbo.Valves", "Type");

        Assert.NotNull(rule);
        Assert.True(rule.Enabled);
        Assert.Equal("dbo.Specific", rule.LookupTable);
    }

    [Fact]
    public void Selector_DisabledSpecificRuleStillDisablesGeneration()
    {
        var settings = new LookupGenerationSettings();
        settings.Rules.Add(R("", "Type", enabled: true));
        settings.Rules.Add(R("dbo.Valves", "Type", enabled: false));

        Assert.False(LookupGenerationRuleSelector.Find(settings, "Valve", "dbo.Valves", "Type").Enabled);
    }

    [Fact]
    public void Selector_MatchesAnySourceField_NotOnlyType()
    {
        var settings = new LookupGenerationSettings();
        settings.Rules.Add(R("dbo.Valves", "Status", enabled: true));
        Assert.NotNull(LookupGenerationRuleSelector.Find(settings, "Valve", "dbo.Valves", "Status"));
        Assert.Null(LookupGenerationRuleSelector.Find(settings, "Valve", "dbo.Valves", "Type"));
    }

    [Fact]
    public void Load_NormalizesNullFieldsInsteadOfFallingBackToDefaults()
    {
        var project = new ConfiguratorProject();
        var settings = new LookupGenerationSettings { AutomaticDiscoveryEnabled = false };
        settings.Rules.Add(R("dbo.Valves", "Type", enabled: true));
        LookupGenerationSettingsStore.Save(project, settings);

        // Simulate a hand-edited file with JSON nulls.
        var node = project.Architecture["lookupGeneration"]!.AsObject();
        node["Rules"]!.AsArray()[0]!["SourceTable"] = null;

        var loaded = LookupGenerationSettingsStore.Load(project);
        Assert.False(loaded.AutomaticDiscoveryEnabled);   // the stored value survived; defaults were not substituted
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsAndMalformedIdentifiers()
    {
        var duplicate = new LookupGenerationSettings();
        var a = R("dbo.Valves", "Type", enabled: true); a.Id = "same";
        var b = R("dbo.Motors", "Type", enabled: true); b.Id = "same";
        duplicate.Rules.AddRange(new[] { a, b });
        Assert.Throws<InvalidDataException>(() => LookupGenerationSettingsStore.Validate(duplicate));

        var malformed = new LookupGenerationSettings();
        malformed.Rules.Add(R("a.b.c", "Type", enabled: true));   // three-part name
        Assert.Throws<InvalidDataException>(() => LookupGenerationSettingsStore.Validate(malformed));
    }

    [Fact]
    public void Import_RejectsOversizedFiles()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, new string(' ', 1024 * 1024 + 10));
            Assert.Throws<InvalidDataException>(() => LookupGenerationSettingsStore.Import(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
