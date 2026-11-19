namespace Containers.Windows.Silos.DescriptionModel;

public sealed record WciConfiguration
{
    /// <summary>Optional WCIFS volume mount point for failed-creation rollback. Defaults to the scratch root.</summary>
    public string? DetachVolumePath { get; init; }

    public uint IsolationMode { get; init; } // Hard (0) or soft (1).
    public uint NestingMode { get; init; } // Inner
    public IReadOnlyList<WciLayer> Layers { get; init; } = [];
    public string ScratchRoot { get; }

    public WciConfiguration(string scratchRoot)
    {
        ScratchRoot = scratchRoot;
    }
}