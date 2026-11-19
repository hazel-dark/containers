namespace Containers.Windows.Silos.DescriptionModel;

/// <summary>Filesystem adapter. Bind maps need no WCI setup. WCI is called only when explicitly configured.</summary>
public sealed record FileSystemConfiguration
{
    public WciConfiguration? Wci { get; init; }
    public IReadOnlyList<BindMapping> Bindings { get; init; } = [];
    public IReadOnlyList<BindBatch> Batches { get; init; } = [];
}