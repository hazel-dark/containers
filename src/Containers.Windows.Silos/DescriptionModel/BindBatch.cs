namespace Containers.Windows.Silos.DescriptionModel;

public sealed record BindBatch
{
    public IReadOnlyList<string> Exceptions { get; init; } = [];
    public string ConfigurationPath { get; }

    public BindBatch(string configurationPath)
    {
        ConfigurationPath = configurationPath;
    }
}