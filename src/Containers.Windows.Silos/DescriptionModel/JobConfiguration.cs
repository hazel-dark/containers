namespace Containers.Windows.Silos.DescriptionModel;

public sealed record JobConfiguration
{
    public CpuConfiguration? Cpu { get; init; }
    public ulong? MemoryLimit { get; init; }
    public string? SystemRoot { get; init; }
    public bool KillOnLastHandleClose { get; init; }
    public bool EnableThreadImpersonation { get; init; }
}