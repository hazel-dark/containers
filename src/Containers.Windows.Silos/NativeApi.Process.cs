using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    public nint Launch(nint job, string command, uint flags)
    {
        var commandLine = (command + '\0').ToCharArray();
        nuint length = NativeJobConstants.SingleProcThreadAttributeListSize;
        var attributes = stackalloc byte[NativeJobConstants.SingleProcThreadAttributeListSize];
        var list = new LPPROC_THREAD_ATTRIBUTE_LIST(attributes);
        CheckBool(PInvoke.InitializeProcThreadAttributeList(list, 1, 0, &length).Value, "InitializeProcThreadAttributeList");

        HANDLE token = default;
        try
        {
            CheckBool(PInvoke.UpdateProcThreadAttribute(list, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, (nuint)sizeof(nint), null, null).Value, "UpdateProcThreadAttribute(JOB_LIST)");
            flags |= (uint)PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT;

            if (IsServer(job))
            {
                var data = new byte[NativeJobConstants.ServerSiloUserSharedDataSize];
                SiloException.CheckNt(QueryJob(job, NativeJobConstants.JobObjectServerSiloUserSharedData, data), "QueryServerSiloUserSharedData");

                var session = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
                SiloException.CheckHResult(session == -1 ? SiloException.FromWin32((int)WIN32_ERROR.ERROR_EMPTY) : 0, "Query server service session");

                const TOKEN_ACCESS_MASK tokenAllAccess = TOKEN_ACCESS_MASK.TOKEN_ALL_ACCESS;
                HANDLE original;
                CheckBool(PInvoke.OpenProcessToken((HANDLE)(nint)NativeJobConstants.NtCurrentProcess, tokenAllAccess, &original).Value, "OpenProcessToken");

                try
                {
                    CheckBool(PInvoke.DuplicateTokenEx(original, tokenAllAccess, null, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, &token).Value, "DuplicateTokenEx");
                    CheckBool(PInvoke.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenSessionId, &session, sizeof(int)).Value, "SetTokenInformation(TokenSessionId)");
                }
                finally
                {
                    Close(original);
                }

                flags |= (uint)PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;
            }

            var startup = new StartupInfoEx { Attributes = list };
            startup.Startup.cb = NativeJobConstants.StartupInfoExSize;
            fixed (char* line = commandLine)
            {
                PROCESS_INFORMATION process;
                CheckBool(PInvoke.CreateProcessAsUser(token, null, line, null, null, true, (PROCESS_CREATION_FLAGS)flags, null, null, &startup.Startup, &process).Value, "CreateProcessAsUserW");
                Close(process.hThread);
                return process.hProcess;
            }
        }
        finally
        {
            Close(token);
            PInvoke.DeleteProcThreadAttributeList(list);
        }
    }
}