using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

internal partial interface ISiloApi
{
    JobSafeHandle CreateJob();
    int SetJob(JobSafeHandle job, int informationClass, Span<byte> data);
    int QueryJob(JobSafeHandle job, int informationClass, Span<byte> data);
    void RegisterServer(JobSafeHandle job);
    void SetSystemRoot(JobSafeHandle job, string root);
    void AssignProcess(JobSafeHandle job, nint process);
    void Boot(JobSafeHandle job);
    void SetupObjects(JobSafeHandle job, ObjectNamespaceConfiguration configuration);
    IFileSystemRegistration CreateFileSystemRegistration(JobSafeHandle job);
    void SetupRegistry(JobSafeHandle job, RegistryConfiguration configuration);
    void SetupNetwork(JobSafeHandle job, string name);
    void SetupMounts(JobSafeHandle job, IReadOnlyList<DriveMapping> mounts);
    void SetupPipes(JobSafeHandle job, IReadOnlyList<PipeMapping> pipes);
    void CleanupFileSystem(JobSafeHandle job, string? volumePath);
    void CleanupNetwork(JobSafeHandle job);
    void CleanupRegistry(JobSafeHandle job);
    void ConfigureServerImage(JobSafeHandle job, DownlevelConfiguration? configuration);
    Guid GetId(JobSafeHandle job);
    string GetObjectRoot(JobSafeHandle job);
    string GetRegistryRoot(JobSafeHandle job);
    nint GetRegistryHandle(JobSafeHandle job, uint type);
    nint Launch(JobSafeHandle job, string command, uint flags);
    void ModifyRegistryFlags(JobSafeHandle job, uint add, uint remove);
    void AddRuntimeVirtualKeys(JobSafeHandle job, IReadOnlyList<RuntimeVirtualKey> keys);
    void Terminate(JobSafeHandle job, int status);
    SiloTerminationNotification RegisterForTerminationNotification(JobSafeHandle job);
}