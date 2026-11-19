namespace Containers.Windows.Silos.DescriptionModel;

public sealed record ObjectDirectory
{
    public DirectoryShadow Shadow { get; init; }

    /// <summary>Optional host object directory from which to copy security.</summary>
    public string? SecurityDescriptorSource { get; init; }

    public IReadOnlyList<ObjectDirectory> Directories { get; init; } = [];
    public IReadOnlyList<ObjectSymlink> Symlinks { get; init; } = [];
    public string Name { get; }

    public ObjectDirectory(string name)
    {
        Name = name;
    }
}