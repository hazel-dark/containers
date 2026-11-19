namespace Containers.Windows.Silos.DescriptionModel;

/// <summary>Managed description, not the opaque C++ Wc description ABI. Null providers are absent;
/// empty provider objects request initialization. Paths are NT paths unless documented otherwise.</summary>
public sealed record SiloDescription
{
    public JobConfiguration? Job { get; init; }
    public ObjectNamespaceConfiguration? ObjectNamespace { get; init; }
    public FileSystemConfiguration? FileSystem { get; init; }
    public RegistryConfiguration? Registry { get; init; }
    public string? NetworkCompartment { get; init; }
    public IReadOnlyList<DriveMapping>? Mounts { get; init; }
    public IReadOnlyList<PipeMapping>? NamedPipes { get; init; }
    public DeviceConfiguration? Device { get; init; }
    public DownlevelConfiguration? Downlevel { get; init; }
}