namespace Configurator.Core.Architecture;

/// <summary>Active project rules shared by Online lookup resolution; Offline reads its own project instance directly.</summary>
public static class LookupGenerationSettingsRuntime
{
    public static LookupGenerationSettings Current { get; set; } = LookupGenerationDefaults.Create();
}
