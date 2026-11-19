namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistryKeyConfiguration
{
    public string? SecurityDescriptorSource { get; init; }
    public bool Volatile { get; init; }
    public IReadOnlyList<RegistryKeyConfiguration> Keys { get; init; } = [];
    public IReadOnlyList<RegistryValue> Values { get; init; } = [];
    public string Name { get; }

    public RegistryKeyConfiguration(string name)
    {
        Name = name;
    }
}