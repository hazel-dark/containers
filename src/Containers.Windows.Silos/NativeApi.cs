using Wdk = Windows.Wdk.PInvoke;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.SystemServices;

namespace Containers.Windows.Silos;

[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
internal sealed unsafe partial class NativeApi : ISiloApi
{
    internal static readonly NativeApi Instance = new NativeApi(new PlatformProvider());
    private readonly PlatformProvider _platform;

    internal NativeApi(PlatformProvider platform) => _platform = platform;

    public JobSafeHandle CreateJob()
    {
        var status = NativeMethods.NtCreateJobObject(out var job, NativeJobConstants.JobObjectAllAccess, null);
        if (status < 0)
        {
            job.Dispose();
            SiloException.CheckNt(status, "NtCreateJobObject");
        }
        return job;
    }

    private void Close(nint handle)
    {
        if (handle != 0 && handle != -1) _platform.Close(handle);
    }

    public int SetJob(JobSafeHandle job, int informationClass, Span<byte> data)
    {
        fixed (byte* p = data)
        {
            var status = _platform.SetJob(job, informationClass, p, (uint)data.Length);
            SiloException.CheckNt(status, $"NtSetInformationJobObject({informationClass})");
            return status;
        }
    }

    public int QueryJob(JobSafeHandle job, int informationClass, Span<byte> data)
    {
        fixed (byte* p = data)
            return _platform.QueryJob(job, informationClass, p, (uint)data.Length, null);
    }

    public void SetSystemRoot(JobSafeHandle job, string root)
    {
        fixed (char* p = root)
        {
            var terminator = root.IndexOf('\0');
            var name = NativeBuffer.UnicodeString(p, terminator < 0 ? root.Length : terminator);
            SiloException.CheckNt(_platform.SetJob(job, NativeJobConstants.JobObjectSiloSystemRoot, &name, (uint)sizeof(UNICODE_STRING)), "SetSiloSystemRoot");
        }
    }

    public void AssignProcess(JobSafeHandle job, nint process) => SiloException.CheckNt(NativeMethods.NtAssignProcessToJobObject(job, process), "NtAssignProcessToJobObject");
    public void Terminate(JobSafeHandle job, int status) => SiloException.CheckNt(NativeMethods.NtTerminateJobObject(job, status), "NtTerminateJobObject");

    private void Ioctl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output = default, int acceptedError = 0)
    {
        var error = _platform.DeviceControl(handle, code, input, output);
        if (error != 0 && error != acceptedError)
            SiloException.CheckWin32(error, $"DeviceIoControl(0x{code:x})");
    }

    public void RegisterServer(JobSafeHandle job)
    {
        const GENERIC_ACCESS_RIGHTS access = GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE;
        using var dam = PInvoke.CreateFile(@"\\.\DamCtrl", (uint)access, 0, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL);
        var jobValue = job.DangerousGetHandle();
        if (dam.DangerousGetHandle() == -1)
            SendSm(job, NativeJobConstants.WcSmRegisterServerSiloApi);
        else if (!PInvoke.DeviceIoControl(new HANDLE(dam.DangerousGetHandle()), NativeJobConstants.WcDamRegisterSiloControlCode, &jobValue, (uint)sizeof(nint), null, 0, null, null))
            SiloException.CheckWin32(GetLastErrorOrDefault(), "Register silo with DAM");
    }

    public void Boot(JobSafeHandle job) => SendSm(job, NativeJobConstants.SmStartServerSiloApi);

    private void SendSm(JobSafeHandle job, uint api)
    {
        nint port = 0;
        try
        {
            SiloException.CheckNt(NativeMethods.RtlConnectToSm(null, 0, 0, out port), "RtlConnectToSm");

            var message = new SmMessage();
            message.ApiNumber = api;
            message.Silo = job.DangerousGetHandle();
            SiloException.CheckNt(NativeMethods.RtlSendMsgToSm(port, &message), $"RtlSendMsgToSm({api})");
        }
        finally
        {
            Close(port);
        }
    }

    public bool IsServer(JobSafeHandle job)
    {
        SERVERSILO_BASIC_INFORMATION info = default;
        // Server 2022 takes the prefix ending immediately before ContainerBuildNumber.
        var status = QueryJob(job, NativeJobConstants.JobObjectServerSiloBasicInformation, info.AsBytes()[..NativeJobConstants.ServerSiloBasicInformationSize]);
        if (status == NTSTATUS.STATUS_JOB_NO_CONTAINER)
            return false;

        SiloException.CheckNt(status, "QueryServerSiloBasicInformation");
        return true;
    }

    public string GetObjectRoot(JobSafeHandle job)
    {
        Span<byte> initial = stackalloc byte[NativeJobConstants.SiloObjectRootInitialBufferSize];
        var size = initial.Length;
        while (true)
        {
            using var data = new NativeScratchBuffer<byte>(initial, size);
            data.Span.Clear();
            fixed (byte* p = data.Span)
            {
                uint returned;
                var status = _platform.QueryJob(job, NativeJobConstants.JobObjectSiloRootDirectory, p, (uint)size, &returned);
                if (status == NTSTATUS.STATUS_BUFFER_TOO_SMALL)
                {
                    size = size == initial.Length
                        ? NativeJobConstants.SiloObjectRootRetryBufferSize
                        : checked(size * 2);
                    continue;
                }

                SiloException.CheckNt(status, "QuerySiloObjectRootDirectory");
                var path = ((SiloObjectRootDirectory*)p)->Path;
                return new string(path.Buffer, 0, path.Length / sizeof(char));
            }
        }
    }

    public Guid GetId(JobSafeHandle job)
    {
        Span<byte> data = stackalloc byte[NativeJobConstants.ContainerIdInformationSize];
        data.Clear();
        SiloException.CheckNt(QueryJob(job, NativeJobConstants.JobObjectContainerId, data), "QueryContainerId");
        return new Guid(data[..sizeof(Guid)]);
    }

    public string GetRegistryRoot(JobSafeHandle job) => FormatRegistryRoot(GetId(job), "");

    private static string FormatRegistryRoot(Guid id, string suffix) => @"\Registry\WC\Silo" + id.ToString("D") + suffix;

    private static int GetLastErrorOrDefault()
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 0 ? (int)WIN32_ERROR.ERROR_ASSERTION_FAILURE : error;
    }

    internal static void CheckBool(int result, string operation)
    {
        if (result == 0)
            SiloException.CheckWin32(GetLastErrorOrDefault(), operation);
    }

    // Native call boundary used by portable provider regression tests.
    internal partial class PlatformProvider
    {
        internal virtual void Close(nint handle) => Wdk.NtClose((HANDLE)handle);

        internal virtual int SetJob(JobSafeHandle job, int informationClass, void* data, uint length)
            => NativeMethods.NtSetInformationJobObject(job, informationClass, data, length);

        internal virtual int QueryJob(JobSafeHandle job, int informationClass, void* data, uint length, uint* returned)
            => NativeMethods.NtQueryInformationJobObject(job, informationClass, data, length, returned);

        internal virtual SafeFileHandle OpenDevice(string name, uint access, nint templateHandle)
        {
            using var template = templateHandle == 0 ? null : new SafeFileHandle(templateHandle, ownsHandle: false);
            var handle = PInvoke.CreateFile(name, access, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, template);
            if (handle.IsInvalid)
            {
                var error = GetLastErrorOrDefault();
                handle.Dispose();
                SiloException.CheckWin32(error, $"CreateFileW({name})");
            }

            return handle;
        }

        internal virtual int DeviceControl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
            => PInvoke.DeviceIoControl(handle, code, input, output, out _, null) ? 0 : GetLastErrorOrDefault();
    }
}