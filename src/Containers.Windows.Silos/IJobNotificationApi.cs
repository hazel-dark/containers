namespace Containers.Windows.Silos;

internal unsafe interface IJobNotificationApi
{
    Guid GetId(JobSafeHandle job);
    bool CompareJobs(JobSafeHandle first, JobSafeHandle second);
    JobSafeHandle DuplicateJob(JobSafeHandle job);
    int SetJob(JobSafeHandle job, int informationClass, Span<byte> data);
    int AllocateJobNotification(out nint notification, JobSafeHandle job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment);
    void WaitForJobNotification(nint notification);
    void ReleaseJobNotification(nint notification);
    bool IsServer(JobSafeHandle job);
}