using System.Runtime.InteropServices;

namespace Containers.Windows.Silos;

internal static unsafe partial class NativeMethods
{
    [LibraryImport("ntdll.dll")] internal static partial int NtCreateJobObject(out nint job, uint access, ObjectAttributes* attributes);
    [LibraryImport("ntdll.dll")] internal static partial int NtClose(nint handle);
    [LibraryImport("ntdll.dll")] internal static partial int NtDuplicateObject(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, uint attributes, uint options);
    [LibraryImport("ntdll.dll")] internal static partial int TpAllocJobNotification(out nint notification, nint job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment);
    [LibraryImport("ntdll.dll")] internal static partial void TpWaitForJobNotification(nint notification);
    [LibraryImport("ntdll.dll")] internal static partial void TpReleaseJobNotification(nint notification);
    [LibraryImport("ntdll.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int RtlQueryRegistryValuesEx(uint relativeTo, string path, RegistryQueryEntry* table, nint context, nint environment);
    [LibraryImport("ntdll.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int RtlDosPathNameToRelativeNtPathName_U_WithStatus(string path, out UnicodeString name, nint filePart, out RelativeNtName relativeName);
    [LibraryImport("ntdll.dll")] internal static partial void RtlReleaseRelativeName(RelativeNtName* name);
    [LibraryImport("ntdll.dll")] internal static partial void RtlFreeUnicodeString(UnicodeString* name);
    [LibraryImport("ntdll.dll")] internal static partial int NtQueryEaFile(nint file, IoStatusBlock* io, void* buffer, uint length, byte singleEntry, void* eaList, uint eaListLength, uint* eaIndex, byte restart);
    [LibraryImport("ntdll.dll")] internal static partial int NtWaitForSingleObject(nint handle, byte alertable, long* timeout);
    [LibraryImport("ntdll.dll")] internal static partial int NtSetInformationJobObject(nint job, int informationClass, void* data, uint length);
    [LibraryImport("ntdll.dll")] internal static partial int NtQueryInformationJobObject(nint job, int informationClass, void* data, uint length, uint* returned);
    [LibraryImport("ntdll.dll")] internal static partial int NtAssignProcessToJobObject(nint job, nint process);
    [LibraryImport("ntdll.dll")] internal static partial int NtTerminateJobObject(nint job, int status);
    [LibraryImport("ntdll.dll")] internal static partial uint RtlNtStatusToDosErrorNoTeb(int status);
    [LibraryImport("ntdll.dll")] internal static partial int RtlConnectToSm(UnicodeString* name, nint handle, uint subsystem, out nint port);
    [LibraryImport("ntdll.dll")] internal static partial int RtlSendMsgToSm(nint port, SmMessage* message);
    [LibraryImport("ntdll.dll")] internal static partial int NtCreateFile(out nint handle, uint access, ObjectAttributes* attributes, out IoStatusBlock io, long* allocationSize, uint fileAttributes, uint shareAccess, uint disposition, uint options, void* ea, uint eaLength);
    [LibraryImport("ntdll.dll")] internal static partial int NtFsControlFile(nint handle, nint ev, nint apc, nint context, out IoStatusBlock io, uint code, void* input, uint inputLength, void* output, uint outputLength);
    [LibraryImport("ntdll.dll")] internal static partial int NtCreateDirectoryObjectEx(out nint handle, uint access, ObjectAttributes* attributes, nint shadow, uint flags);
    [LibraryImport("ntdll.dll")] internal static partial int NtOpenDirectoryObject(out nint handle, uint access, ObjectAttributes* attributes);
    [LibraryImport("ntdll.dll")] internal static partial int NtCreateSymbolicLinkObject(out nint handle, uint access, ObjectAttributes* attributes, UnicodeString* target);
    [LibraryImport("ntdll.dll")] internal static partial int NtOpenSymbolicLinkObject(out nint handle, uint access, ObjectAttributes* attributes);
    [LibraryImport("ntdll.dll")] internal static partial int NtQuerySymbolicLinkObject(nint handle, UnicodeString* target, uint* length);
    [LibraryImport("ntdll.dll")] internal static partial int NtSetInformationSymbolicLink(nint handle, int informationClass, void* data, uint size);
    [LibraryImport("ntdll.dll")] internal static partial int NtQuerySecurityObject(nint handle, uint information, void* security, uint length, out uint needed);
    [LibraryImport("ntdll.dll")] internal static partial int NtCreateKey(out nint key, uint access, ObjectAttributes* attributes, uint title, UnicodeString* keyClass, uint options, out uint disposition);
    [LibraryImport("ntdll.dll")] internal static partial int NtOpenKey(out nint key, uint access, ObjectAttributes* attributes);
    [LibraryImport("ntdll.dll")] internal static partial int NtDeleteKey(nint key);
    [LibraryImport("ntdll.dll")] internal static partial int NtQueryKey(nint key, int informationClass, void* data, uint length, out uint needed);
    [LibraryImport("ntdll.dll")] internal static partial int NtEnumerateKey(nint key, uint index, int informationClass, void* data, uint length, out uint needed);
    [LibraryImport("ntdll.dll")] internal static partial int NtSetValueKey(nint key, UnicodeString* name, uint title, uint type, void* data, uint length);
    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateKey")] internal static partial int NtCreateProviderKey(nint* key, uint access, ObjectAttributes* attributes, uint title, UnicodeString* keyClass, uint options, uint* disposition);
    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateSymbolicLinkObject")] internal static partial int NtCreateProviderSymbolicLink(nint* handle, uint access, ObjectAttributes* attributes, UnicodeString* target);

    [LibraryImport("iphlpapi.dll")] internal static partial void InitializeCompartmentEntry(CompartmentEntry* entry);
    [LibraryImport("iphlpapi.dll")] internal static partial uint CreateCompartment(CompartmentEntry* entry);
    [LibraryImport("iphlpapi.dll")] internal static partial uint DeleteCompartment(uint id);
    [LibraryImport("iphlpapi.dll")] internal static partial uint GetJobCompartmentId(nint job);
    [LibraryImport("iphlpapi.dll")] internal static partial uint SetJobCompartmentId(nint job, uint id);
    
    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfSetupFilter(nint job, uint flags, string root, string target, nint* exceptions, uint count);
    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfRemoveMapping(nint job, string root);
    [LibraryImport("wc_storage.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int WcAttachFilterEx(nint job, uint isolationMode, string volume, WciLayerDescriptor* layers, uint count, uint nestingMode);

    [LibraryImport("bindfltapi.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial int BfAttachFilter(string volume, int* attached);
    [LibraryImport("bindfltapi.dll")] internal static partial int BfSetupFilterBatched(nint job, nint context, byte* configuration, uint size, uint flags, uint exceptionCount, nint* exceptions);
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    internal global::Windows.Win32.System.Threading.STARTUPINFOW Startup;
    internal global::Windows.Win32.System.Threading.LPPROC_THREAD_ATTRIBUTE_LIST Attributes;
}
