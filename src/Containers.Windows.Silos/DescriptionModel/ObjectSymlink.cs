namespace Containers.Windows.Silos.DescriptionModel;

public sealed record ObjectSymlink
{
    /// <summary>Exactly one of Target and TargetSource must be supplied.</summary>
    public string? Target { get; init; }

    public string? TargetSource { get; init; }
    public SymlinkScope Scope { get; init; }
    public uint AccessMask { get; init; }
    public string Name { get; }

    public ObjectSymlink(string name)
    {
        Name = name;
    }
}