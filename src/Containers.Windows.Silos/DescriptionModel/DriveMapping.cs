namespace Containers.Windows.Silos.DescriptionModel;

public sealed record DriveMapping
{
    public char Drive { get; }
    public string Target { get; }

    public DriveMapping(char drive, string target)
    {
        Drive = drive;
        Target = target;
    }
}