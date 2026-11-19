using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

internal partial interface ISiloApi
{
    nint CreateJob();
    void Close(nint handle);
    int SetJob(nint job, int informationClass, Span<byte> data);
    int QueryJob(nint job, int informationClass, Span<byte> data);
    void RegisterServer(nint job);
    void SetSystemRoot(nint job, string root);
    void AssignProcess(nint job, nint process);
    void Boot(nint job);
    void SetupObjects(nint job, ObjectNamespaceConfiguration configuration);
    IFileSystemRegistration CreateFileSystemRegistration(nint job);
    void SetupRegistry(nint job, RegistryConfiguration configuration);
    void SetupNetwork(nint job, string name);
    void SetupMounts(nint job, IReadOnlyList<DriveMapping> mounts);
    void SetupPipes(nint job, IReadOnlyList<PipeMapping> pipes);
    void CleanupFileSystem(nint job, string? volumePath);
    void CleanupNetwork(nint job);
    void CleanupRegistry(nint job);
    void ConfigureServerImage(nint job, DownlevelConfiguration? configuration);
    Guid GetId(nint job);
    string GetObjectRoot(nint job);
    string GetRegistryRoot(nint job);
    nint GetRegistryHandle(nint job, uint type);
    nint Launch(nint job, string command, uint flags);
    void ModifyRegistryFlags(nint job, uint add, uint remove);
    void AddRuntimeVirtualKeys(nint job, IReadOnlyList<RuntimeVirtualKey> keys);
    void Terminate(nint job, int status);
    SiloTerminationNotification RegisterForTerminationNotification(nint job);
}