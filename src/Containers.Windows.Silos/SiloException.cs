using System.ComponentModel;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;

namespace Containers.Windows.Silos;

/// <summary>Carries the native error identity and the corresponding Wc HRESULT.</summary>
public sealed class SiloException : Exception
{
    public string Operation { get; }
    public int? NtStatus { get; }
    public int? Win32Error { get; }

    private SiloException(string operation, string message, int hresult, int? ntStatus = null, int? win32Error = null)
        : base(operation + " returned " + message)
    {
        Operation = operation;
        NtStatus = ntStatus;
        Win32Error = win32Error;
        HResult = hresult;
    }

    internal static int MapNtStatus(int status, uint dosError)
    {
        if (status >= 0)
            return 0;

        if (status == NTSTATUS.STATUS_NO_MEMORY)
            return HRESULT.E_OUTOFMEMORY;

        return dosError == 0 || dosError == (uint)WIN32_ERROR.ERROR_MR_MID_NOT_FOUND
            ? status | (int)FACILITY_CODE.FACILITY_NT_BIT
            : FromWin32(unchecked((int)dosError));
    }

    internal static void CheckNt(int status, string operation)
    {
        if (status >= 0)
            return;

        var dosError = OperatingSystem.IsWindows() ? NativeMethods.RtlNtStatusToDosErrorNoTeb(status) : 0;
        throw new SiloException(operation, $"NTSTATUS 0x{status:x8}", MapNtStatus(status, dosError), ntStatus: status);
    }

    // SDK winerror.h HRESULT_FROM_WIN32: severity bit, FACILITY_WIN32, low WORD.
    internal static int FromWin32(int error) => error <= 0 ? error : int.MinValue | ((int)FACILITY_CODE.FACILITY_WIN32 << 16) | (error & ushort.MaxValue);

    internal static void CheckWin32(int error, string operation)
    {
        if (error != 0)
            throw new SiloException(operation, $"Win32 0x{error:x8} ({new Win32Exception(error).Message})", FromWin32(error), win32Error: error);
    }

    internal static void CheckHResult(int hr, string operation)
    {
        if (hr < 0)
            throw new SiloException(operation, $"HRESULT 0x{hr:x8}", hr);
    }
}