namespace Containers.Windows.Silos.DescriptionModel;

public sealed record CpuConfiguration
{
    public uint Weight { get; }
    public uint MaximumRate { get; }

    public CpuConfiguration(uint weight = 0, uint maximumRate = 0)
    {
        Weight = weight;
        MaximumRate = maximumRate;
    }
}