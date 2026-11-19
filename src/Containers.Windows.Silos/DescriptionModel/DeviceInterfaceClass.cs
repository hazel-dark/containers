namespace Containers.Windows.Silos.DescriptionModel;

public sealed record DeviceInterfaceClass
{
    public DeviceInterfaceClassType Type { get; init; }
    public Guid Id { get; init; }
    public bool Recurse { get; init; }

    public DeviceInterfaceClass(DeviceInterfaceClassType type, Guid id, bool recurse)
    {
        Type = type;
        Id = id;
        Recurse = recurse;
    }
}