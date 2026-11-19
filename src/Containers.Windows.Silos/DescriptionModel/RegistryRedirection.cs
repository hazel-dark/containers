namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistryRedirection
{
    public string? Target { get; init; }
    public string? HiveStack { get; init; }
    public uint AccessMask { get; init; }
    public bool Trusted { get; init; }
    public bool ExitNode { get; init; }
    public string Path { get; }

    public RegistryRedirection(string path)
    {
        Path = path;
    }
}