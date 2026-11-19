namespace Containers.Windows.Silos.DescriptionModel;

public sealed record BindMapping
{
    public uint Flags { get; } = NativeProviderConstants.BindFltFlagUseCurrentSiloMapping;
    public IReadOnlyList<string> Exceptions { get; } = [];
    public string Root { get; }
    public string Target { get; }

    public BindMapping(string root, string target)
    {
        Root = root;
        Target = target;
    }
}