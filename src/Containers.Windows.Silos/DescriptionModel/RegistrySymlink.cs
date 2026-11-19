namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RegistrySymlink
{
    public string Path { get; }
    public string Target { get; }

    public RegistrySymlink(string path, string target)
    {
        Path = path;
        Target = target;
    }
}