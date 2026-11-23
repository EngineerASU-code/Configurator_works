namespace Configurator;

internal sealed class FunctionalBlockDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InterfaceText { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;

    public FunctionalBlockDefinition Copy(string name) => new()
    {
        Name = name,
        Description = Description,
        InterfaceText = InterfaceText,
        SourceText = SourceText
    };
}
