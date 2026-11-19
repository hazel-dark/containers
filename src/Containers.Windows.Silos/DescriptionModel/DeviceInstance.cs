namespace Containers.Windows.Silos.DescriptionModel;

public sealed record DeviceInstance
{
    public string Id { get; }
    public string LocationPath { get; }
    public string PortName { get; }
    public IReadOnlyList<DeviceInterfaceClass> InterfaceClasses { get; }

    public DeviceInstance(string id, string locationPath, string portName, IReadOnlyList<DeviceInterfaceClass> interfaceClasses)
    {
        Id = id;
        LocationPath = locationPath;
        PortName = portName;
        InterfaceClasses = interfaceClasses;
    }
}