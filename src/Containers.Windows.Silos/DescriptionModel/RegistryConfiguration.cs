namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistryConfiguration
{
    public IReadOnlyList<RegistryHiveStack> HiveStacks { get; init; } = [];
    public IReadOnlyList<RegistryRedirection> Redirections { get; init; } = [];
    public IReadOnlyList<RegistrySymlink> Symlinks { get; init; } = [];
}