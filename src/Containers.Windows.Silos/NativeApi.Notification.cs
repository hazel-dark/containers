using Windows.Win32;
using Windows.Win32.Foundation;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi : IJobNotificationApi
{
    private static readonly JobNotificationRegistrations NotificationRegistrations = new JobNotificationRegistrations();

    bool IJobNotificationApi.CompareJobs(nint first, nint second)
    {
        if (PInvoke.CompareObjectHandles(new HANDLE(first), new HANDLE(second)) != 0)
            return true;

        var error = GetLastErrorOrDefault();
        if (error != (int)WIN32_ERROR.ERROR_NOT_SAME_OBJECT)
            SiloException.CheckWin32(error, "CompareObjectHandles");
        return false;
    }

    nint IJobNotificationApi.DuplicateJob(nint job)
    {
        SiloException.CheckNt(NativeMethods.NtDuplicateObject(NativeJobConstants.NtCurrentProcess, job, NativeJobConstants.NtCurrentProcess, out var duplicate, 0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS), "Duplicate silo job for notification");
        return duplicate;
    }

    public int AllocateJobNotification(out nint notification, nint job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment) =>
        NativeMethods.TpAllocJobNotification(out notification, job, callback, context, environment);

    public void WaitForJobNotification(nint notification) => NativeMethods.TpWaitForJobNotification(notification);

    public void ReleaseJobNotification(nint notification) => NativeMethods.TpReleaseJobNotification(notification);

    public SiloTerminationNotification RegisterForTerminationNotification(nint job) => new SiloTerminationNotification(job, this, NotificationRegistrations);
}