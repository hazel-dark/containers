using Microsoft.Win32.SafeHandles;
using Wdk = Windows.Wdk.PInvoke;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi : IJobNotificationApi
{
    private static readonly JobNotificationRegistrations NotificationRegistrations = new JobNotificationRegistrations();

    bool IJobNotificationApi.CompareJobs(JobSafeHandle first, JobSafeHandle second)
    {
        if (PInvoke.CompareObjectHandles(first, second) != 0)
            return true;

        var error = GetLastErrorOrDefault();
        if (error != (int)WIN32_ERROR.ERROR_NOT_SAME_OBJECT)
            SiloException.CheckWin32(error, "CompareObjectHandles");
        return false;
    }

    JobSafeHandle IJobNotificationApi.DuplicateJob(JobSafeHandle job)
    {
        using var process = new SafeProcessHandle(NativeJobConstants.NtCurrentProcess, ownsHandle: false);
        var status = Wdk.NtDuplicateObject(process, job, process, out var duplicate, 0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS);
        using (duplicate)
        {
            SiloException.CheckNt(status, "Duplicate silo job for notification");
            // CsWin32 projects the duplicate as a SafeFileHandle. Transfer its single owned reference.
            var result = new JobSafeHandle(duplicate.DangerousGetHandle(), ownsHandle: true);
            duplicate.SetHandleAsInvalid();
            return result;
        }
    }

    public int AllocateJobNotification(out nint notification, JobSafeHandle job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment) =>
        NativeMethods.TpAllocJobNotification(out notification, job, callback, context, environment);

    public void WaitForJobNotification(nint notification) => NativeMethods.TpWaitForJobNotification(notification);

    public void ReleaseJobNotification(nint notification) => NativeMethods.TpReleaseJobNotification(notification);

    public SiloTerminationNotification RegisterForTerminationNotification(JobSafeHandle job) => new SiloTerminationNotification(job, this, NotificationRegistrations);
}