namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistryHive
{
    public string? ExistingPath { get; init; }
    public string FilePath { get; init; } = "";
    public Guid MountId { get; init; }
    public bool ReadOnly { get; init; }
    public bool Immutable { get; init; }
    public bool InheritTrust { get; init; }
    public bool WriteThrough { get; init; }
    public bool TrustedHive { get; init; }
    public ulong FileAccessToken { get; init; }
}