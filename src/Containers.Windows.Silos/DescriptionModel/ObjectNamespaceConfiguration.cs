namespace Containers.Windows.Silos.DescriptionModel;

public sealed record ObjectNamespaceConfiguration
{
    public bool ShadowRoot { get; init; }
    public IReadOnlyList<ObjectDirectory> Directories { get; init; } = [];
    public IReadOnlyList<ObjectSymlink> Symlinks { get; init; } = [];
}