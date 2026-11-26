using Windows.Win32.Foundation;
using Windows.Wdk.Foundation;
using System.Runtime.InteropServices;

namespace Containers.Windows.Silos;

#pragma warning disable SYSLIB1054 // Use LibraryImport

internal static unsafe partial class NativeMethods
{
    // The LibraryImport generator cannot resolve types emitted by CsWin32 in the same compilation.
    [DllImport("ntdll.dll", ExactSpelling = true)] internal static extern int NtCreateJobObject(out JobSafeHandle job, uint access, OBJECT_ATTRIBUTES* attributes);
    [LibraryImport("ntdll.dll")] internal static partial int TpAllocJobNotification(out nint notification, JobSafeHandle job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment);
    [LibraryImport("ntdll.dll")] internal static partial void TpWaitForJobNotification(nint notification);
    [LibraryImport("ntdll.dll")] internal static partial void TpReleaseJobNotification(nint notification);
    [LibraryImport("ntdll.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int RtlQueryRegistryValuesEx(uint relativeTo, string path, RegistryQueryEntry* table, nint context, nint environment);
    [DllImport("ntdll.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern int RtlDosPathNameToRelativeNtPathName_U_WithStatus(string path, out UNICODE_STRING name, nint filePart, out RelativeNtName relativeName);
    [LibraryImport("ntdll.dll")] internal static partial void RtlReleaseRelativeName(RelativeNtName* name);
    [LibraryImport("ntdll.dll")] internal static partial int NtSetInformationJobObject(JobSafeHandle job, int informationClass, void* data, uint length);
    [LibraryImport("ntdll.dll")] internal static partial int NtQueryInformationJobObject(JobSafeHandle job, int informationClass, void* data, uint length, uint* returned);
    [LibraryImport("ntdll.dll")] internal static partial int NtAssignProcessToJobObject(JobSafeHandle job, nint process);
    [LibraryImport("ntdll.dll")] internal static partial int NtTerminateJobObject(JobSafeHandle job, int status);
    [DllImport("ntdll.dll", ExactSpelling = true)] internal static extern int RtlConnectToSm(UNICODE_STRING* name, nint handle, uint subsystem, out nint port);
    [LibraryImport("ntdll.dll")] internal static partial int RtlSendMsgToSm(nint port, SmMessage* message);
    [DllImport("ntdll.dll", ExactSpelling = true)] internal static extern int NtCreateDirectoryObjectEx(out nint handle, uint access, OBJECT_ATTRIBUTES* attributes, nint shadow, uint flags);
    [LibraryImport("ntdll.dll")] internal static partial int NtSetInformationSymbolicLink(nint handle, int informationClass, void* data, uint size);
    [DllImport("ntdll.dll", ExactSpelling = true)] internal static extern int NtCreateSymbolicLinkObject(nint* handle, uint access, OBJECT_ATTRIBUTES* attributes, UNICODE_STRING* target);

    [LibraryImport("iphlpapi.dll")] internal static partial void InitializeCompartmentEntry(CompartmentEntry* entry);
    [LibraryImport("iphlpapi.dll")] internal static partial uint CreateCompartment(CompartmentEntry* entry);
    [LibraryImport("iphlpapi.dll")] internal static partial uint DeleteCompartment(uint id);

    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfSetupFilter(JobSafeHandle job, uint flags, string root, string target, nint* exceptions, uint count);
    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfRemoveMapping(JobSafeHandle job, string root);
    [LibraryImport("wc_storage.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int WcAttachFilterEx(JobSafeHandle job, uint isolationMode, string volume, WciLayerDescriptor* layers, uint count, uint nestingMode);

    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfAttachFilter(string volume, int* attached);
    [LibraryImport("bindfltapi.dll")] internal static partial int BfSetupFilterBatched(JobSafeHandle job, nint context, byte* configuration, uint size, uint flags, uint exceptionCount, nint* exceptions);
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    internal global::Windows.Win32.System.Threading.STARTUPINFOW Startup;
    internal global::Windows.Win32.System.Threading.LPPROC_THREAD_ATTRIBUTE_LIST Attributes;
}

#pragma warning restore