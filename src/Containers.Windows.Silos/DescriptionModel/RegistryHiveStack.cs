namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistryHiveStack
{
    public IReadOnlyList<RegistryHive> Hives { get; init; } = [];
    public IReadOnlyList<RegistryKeyConfiguration> Keys { get; init; } = [];
    public IReadOnlyList<string> DeleteKeys { get; init; } = [];
    public string Name { get; }

    public RegistryHiveStack(string name)
    {
        Name = name;
    }
}