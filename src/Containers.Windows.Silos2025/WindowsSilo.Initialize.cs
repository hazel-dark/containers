using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Win32.System.JobObjects;

namespace Containers.Windows.Silos;

public partial class WindowsSilo
{
    private void SetServerInitialize(nint jobHandle, nint deleteEvent)
    {
        var imageBuild = _api.ValidateBootImage(jobHandle);
        var initialize = new ServerSiloInitialize2025 { DeleteEvent = deleteEvent, ImageBuild = imageBuild };
        _api.SetJob(jobHandle, NativeJobConstants.JobObjectServerSiloInitialize, initialize.AsBytes());
    }

    /// <summary>Returns the server silo's service session identifier, including its unassigned value.</summary>
    public uint ServiceSessionId => Use(handle =>
    {
        Span<byte> data = stackalloc byte[NativeJobConstants.ServerSiloUserSharedDataSize];
        data.Clear();
        SiloException.CheckNt(_api.QueryJob(handle, NativeJobConstants.JobObjectServerSiloUserSharedData, data), "Query server service session");
        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    });

    /// <summary>Indicates whether the job's lifetime total process count is zero.</summary>
    public bool IsFirstBoot => Use(handle =>
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting = default;
        SiloException.CheckNt(_api.QueryJob(handle, (int)JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation, accounting.AsBytes()), "Query job basic accounting");
        return accounting.TotalProcesses == 0;
    });
    
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct ServerSiloInitialize2025
    {
        [FieldOffset(0)] internal nint DeleteEvent;
        [FieldOffset(8)] internal uint ImageBuild;
    }
}