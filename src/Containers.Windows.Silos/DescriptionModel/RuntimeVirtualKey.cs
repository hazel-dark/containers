namespace Containers.Windows.Silos.DescriptionModel;

public sealed record RuntimeVirtualKey
{
    public string Path { get; }
    public string Target { get; }
    public uint AccessMask { get; }

    public RuntimeVirtualKey(string path, string target, uint accessMask = 0)
    {
        Path = path;
        Target = target;
        AccessMask = accessMask;
    }
}