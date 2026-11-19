namespace Containers.Windows.Silos.DescriptionModel;

public sealed record DeviceConfiguration
{
    public bool RequiresDriverStore { get; init; }
    public IReadOnlyList<DeviceCategory> Categories { get; init; } = [];
    public IReadOnlyList<DeviceInstance> Instances { get; init; } = [];
}