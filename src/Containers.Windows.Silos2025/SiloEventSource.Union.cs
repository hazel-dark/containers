using System.Diagnostics.Tracing;

namespace Containers.Windows.Silos;

internal sealed partial class SiloEventSource
{
    [Event(7, Level = EventLevel.Warning)]
    public void UnionRemovalFailed(string path, int result) => WriteEvent(7, path, result);

    [Event(8, Level = EventLevel.Warning)]
    public void MigrationConfigDeleteFailed(string path, int error) => WriteEvent(8, path, error);
}
