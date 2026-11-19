namespace Containers.Windows.Silos.DescriptionModel;

/// <summary>Maps the specified files from the host Windows directory to the existing container image files.</summary>
public sealed record DownlevelConfiguration
{
    public string? BaseImagePath { get; init; }
    public string ContainerRoot { get; }
    public IReadOnlyList<string> HostFiles { get; }

    /// <summary>Maps the specified files from the host Windows directory to the existing container image files.</summary>
    public DownlevelConfiguration(string containerRoot, IReadOnlyList<string> hostFiles)
    {
        ContainerRoot = containerRoot;
        HostFiles = hostFiles;
    }
}