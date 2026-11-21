using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Containers.Windows.Silos;

internal static unsafe partial class NativeMethods
{
    [LibraryImport("ntdll.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial byte RtlDoesFileExists_U(string path);

    [LibraryImport("unionfsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CreateFileSystemUnion(Guid* id, nint* paths, uint count, uint options, JobSafeHandle job, nint reserved);

    [LibraryImport("unionfsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int MigrateFileSystemUnion(nint* paths, uint count, string configPath, Guid* id, JobSafeHandle job, nint reserved);

    [LibraryImport("unionfsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int RemoveFileSystemUnion(Guid* id, uint options, string configPath);

    [LibraryImport("offreg.dll", EntryPoint = "ORCreateHiveEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint CreateOfflineHive(out nint hive, uint options);

    [LibraryImport("offreg.dll", EntryPoint = "ORCloseHive")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint CloseOfflineHive(nint hive);

    [LibraryImport("offreg.dll", EntryPoint = "ORSaveHiveToHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint SaveOfflineHive(nint hive, SafeFileHandle file, uint major, uint minor, uint options);

    [LibraryImport("offreg.dll", EntryPoint = "OROpenKey", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint OpenOfflineKey(nint parent, string name, out nint key);

    [LibraryImport("offreg.dll", EntryPoint = "ORCreateKey", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint CreateOfflineKey(nint parent,
        string name,
        char* keyClass,
        uint options,
        void* security,
        out nint key,
        uint* disposition);

    [LibraryImport("offreg.dll", EntryPoint = "ORCloseKey")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint CloseOfflineKey(nint key);

    [LibraryImport("offreg.dll", EntryPoint = "ORSetValue", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint SetOfflineValue(nint key, string name, uint type, void* data, uint size);
}