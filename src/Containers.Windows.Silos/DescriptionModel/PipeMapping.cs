namespace Containers.Windows.Silos.DescriptionModel;

public sealed record PipeMapping
{
    public string Name { get; }
    public string Target { get; }

    public PipeMapping(string name, string target)
    {
        Name = name;
        Target = target;
    }
}