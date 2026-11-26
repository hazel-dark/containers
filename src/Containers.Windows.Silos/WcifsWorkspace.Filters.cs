using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Containers.Windows.Silos;

public static unsafe partial class WcifsWorkspace
{
    private const int ErrorNoToken = 1008;
    private const int ErrorNotAllAssigned = 1300;

    private const int AlreadyExists = unchecked((int)0x800700B7); // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
    private const int ServiceAlreadyRunning = unchecked((int)0x80070420); // HRESULT_FROM_WIN32(ERROR_SERVICE_ALREADY_RUNNING)

    private static void EnsureFiltersLoaded()
    {
        HANDLE previousToken = default;
        HANDLE processToken = default;
        HANDLE temporaryToken = default;
        try
        {
            const TOKEN_ACCESS_MASK duplicate = TOKEN_ACCESS_MASK.TOKEN_DUPLICATE;
            const TOKEN_ACCESS_MASK impersonate = TOKEN_ACCESS_MASK.TOKEN_IMPERSONATE;
            if (!PInvoke.OpenThreadToken(PInvoke.GetCurrentThread(), duplicate | impersonate, true, &previousToken))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorNoToken)
                    throw Win32Error("OpenThreadToken", error);

                if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), duplicate, &processToken))
                    throw Win32Error("OpenProcessToken", Marshal.GetLastPInvokeError());
            }

            var effectiveToken = previousToken.IsNull ? processToken : previousToken;
            if (!PInvoke.DuplicateTokenEx(effectiveToken, impersonate | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES, null, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenImpersonation, &temporaryToken))
                throw Win32Error("DuplicateTokenEx", Marshal.GetLastPInvokeError());

            EnablePrivilege(temporaryToken, "SeLoadDriverPrivilege");
            LoadFilters(temporaryToken, previousToken);
        }
        finally
        {
            if (!temporaryToken.IsNull)
                PInvoke.CloseHandle(temporaryToken);
            if (!processToken.IsNull)
                PInvoke.CloseHandle(processToken);
            if (!previousToken.IsNull)
                PInvoke.CloseHandle(previousToken);
        }
    }

    private static void EnablePrivilege(HANDLE token, string privilege)
    {
        if (!PInvoke.LookupPrivilegeValue(null, privilege, out var luid))
            throw Win32Error("LookupPrivilegeValue", Marshal.GetLastPInvokeError());

        var request = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
        };
        request.Privileges[0].Luid = luid;
        request.Privileges[0].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;

        var adjusted = PInvoke.AdjustTokenPrivileges(token, false, &request, 0, null, null);
        var error = Marshal.GetLastPInvokeError();
        if (!adjusted || error == ErrorNotAllAssigned)
            throw Win32Error($"Enable {privilege}", error);
    }

    private static void LoadFilters(HANDLE privilegeEnabledToken, HANDLE previousToken)
    {
        if (!PInvoke.SetThreadToken(null, privilegeEnabledToken))
            throw Win32Error("SetThreadToken", Marshal.GetLastPInvokeError());

        try
        {
            EnsureLoaded("wcifs");
            EnsureLoaded("bindflt");
        }
        finally
        {
            // A null handle clears impersonation when there was no prior thread token.
            if (!PInvoke.SetThreadToken(null, previousToken))
                Debug.Fail($"Restoring the WCIFS caller's thread token failed (Windows error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static Win32Exception Win32Error(string operation, int error) => new Win32Exception(error, $"{operation} failed. {Marshal.GetPInvokeErrorMessage(error)}");

    private static void EnsureLoaded(string serviceName)
    {
        int result;
        fixed (char* name = serviceName)
            result = PInvoke.FilterLoad(new PCWSTR(name)).Value;
        if (result < 0 && result is not (AlreadyExists or ServiceAlreadyRunning))
            throw new COMException($"FilterLoad could not load {serviceName} (0x{result:X8}).", result);
    }
}