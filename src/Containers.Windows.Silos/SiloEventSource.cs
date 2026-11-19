using System.Diagnostics.Tracing;

namespace Containers.Windows.Silos;

[EventSource(Name = "Containers-Windows-Silos")]
internal sealed partial class SiloEventSource : EventSource
{
    internal static readonly SiloEventSource Log = new SiloEventSource();

    [Event(1, Level = EventLevel.Warning)]
    public void RegistryCleanupFailed(string operation, int ntStatus, int win32Error, int hresult)
        => WriteEvent(1, operation, ntStatus, win32Error, hresult);

    [Event(2, Level = EventLevel.Warning)]
    public void ObjectLinkSourceFailed(string name, string source, string message, int ntStatus, int hresult)
        => WriteEvent(2, name, source, message, ntStatus, hresult);

    [Event(3, Level = EventLevel.Error)]
    public void UnexpectedSecuritySizeStatus(int ntStatus) => WriteEvent(3, ntStatus);
    
    [Event(4, Level = EventLevel.Warning)]
    public void WciDetachFailed(string path, int hresult) => WriteEvent(4, path, hresult);

    [Event(5, Level = EventLevel.Warning)]
    public void NetworkCleanupFailed(uint id, string operation, uint error) => WriteEvent(5, id, operation, error);

    [Event(6, Level = EventLevel.Warning)]
    public void ModeQueryFailed(int hresult) => WriteEvent(6, hresult);

}