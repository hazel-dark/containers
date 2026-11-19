namespace Containers.Windows.Silos.DescriptionModel;

public sealed record DeviceCategory
{
    public string Name { get; }
    public IReadOnlyList<DeviceInterfaceClass> InterfaceClasses { get; }

    public DeviceCategory(string name, IReadOnlyList<DeviceInterfaceClass> interfaceClasses)
    {
        Name = name;
        InterfaceClasses = interfaceClasses;
    }
}