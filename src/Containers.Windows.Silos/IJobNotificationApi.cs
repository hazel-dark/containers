namespace Containers.Windows.Silos;

internal unsafe interface IJobNotificationApi
{
    Guid GetId(nint job);
    bool CompareJobs(nint first, nint second);
    nint DuplicateJob(nint job);
    int SetJob(nint job, int informationClass, Span<byte> data);
    int AllocateJobNotification(out nint notification, nint job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment);
    void WaitForJobNotification(nint notification);
    void ReleaseJobNotification(nint notification);
    bool IsServer(nint job);
    void Close(nint handle);
}