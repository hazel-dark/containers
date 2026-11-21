namespace Containers.Windows.Silos;

public partial class WindowsSilo
{
    private void SetServerInitialize(JobSafeHandle jobHandle, nint deleteEvent)
    {
        var initialize = new ServerSiloInitialize { DeleteEvent = deleteEvent };
        _api.SetJob(jobHandle, NativeJobConstants.JobObjectServerSiloInitialize, initialize.AsBytes());
    }
}