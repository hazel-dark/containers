namespace Containers.Windows.Silos.DescriptionModel;

public sealed record WciLayer
{
    public Guid Id { get; }
    public string Path { get; }

    public WciLayer(Guid id, string path)
    {
        Id = id;
        Path = path;
    }
}