using System;
using System.IO;
using System.Linq;
using Configurator.Core.Architecture;
using Configurator.Core.Projects;
using Configurator.Infrastructure.Projects;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class LookupGenerationSettingsTests
{
    [Fact]
    public void SaveAndLoad_StoresRulesInsideProjectArchitecture()
    {
        var project = new ConfiguratorProject();
        var settings = LookupGenerationDefaults.Create();
        var valve = settings.Rules.Single(x => x.ClassName == "Valve");
        valve.LookupTable = "dbo.CustomValveKinds";
        valve.KeyField = "KindCode";
        valve.DisplayField = "Caption";

        LookupGenerationSettingsStore.Save(project, settings);
        var loaded = LookupGenerationSettingsStore.Load(project);
        var restored = loaded.Rules.Single(x => x.ClassName == "Valve");

        Assert.Equal("dbo.CustomValveKinds", restored.LookupTable);
        Assert.Equal("KindCode", restored.KeyField);
        Assert.Equal("Caption", restored.DisplayField);
        Assert.NotNull(project.Architecture["lookupGeneration"]);
    }

    [Fact]
    public void DisabledRule_IsPersistedWithoutRemovingLegacyDefaults()
    {
        var project = new ConfiguratorProject();
        var settings = LookupGenerationDefaults.Create();
        settings.Rules.Single(x => x.ClassName == "Valve").Enabled = false;
        LookupGenerationSettingsStore.Save(project, settings);

        var loaded = LookupGenerationSettingsStore.Load(project);
        Assert.False(loaded.Rules.Single(x => x.ClassName == "Valve").Enabled);
        Assert.Contains(loaded.Rules, x => x.ClassName == "DSV");
    }

    [Fact]
    public void Validation_RejectsActiveRuleWithoutStableKey()
    {
        var settings = new LookupGenerationSettings
        {
            Rules = { new LookupGenerationRule { ClassName = "Valve", SourceField = "Type", LookupTable = "dbo.ValveType", KeyField = "" } }
        };
        Assert.Throws<InvalidDataException>(() => LookupGenerationSettingsStore.Validate(settings));
    }

    [Fact]
    public void ExportImport_RoundTripsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = LookupGenerationDefaults.Create();
            settings.AutomaticDiscoveryEnabled = false;
            LookupGenerationSettingsStore.Export(path, settings);
            var imported = LookupGenerationSettingsStore.Import(path);
            Assert.False(imported.AutomaticDiscoveryEnabled);
            Assert.Equal(settings.Rules.Count, imported.Rules.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
