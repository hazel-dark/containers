using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32;
using Windows.Win32.Foundation;
using Wdk = Windows.Wdk.PInvoke;
using Windows.Win32.System.Registry;
using Windows.Win32.Storage.FileSystem;
using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    private const string CurrentVersion = @"Microsoft\Windows NT\CurrentVersion";
    private static ReadOnlySpan<byte> CatalogHintEaName => "$CI.CatalogHint"u8;
    private static readonly int EaQueryNameOffset = (int)Marshal.OffsetOf<FILE_GET_EA_INFORMATION>(nameof(FILE_GET_EA_INFORMATION.EaName));
    private static readonly int CatalogHintValueOffset = (int)Marshal.OffsetOf<FILE_FULL_EA_INFORMATION>(nameof(FILE_FULL_EA_INFORMATION.EaName)) + CatalogHintEaName.Length + 1;

    internal readonly record struct ImageVersion
    {
        public uint Build { get; }
        public uint Revision { get; }

        public ImageVersion(uint build, uint revision)
        {
            Build = build;
            Revision = revision;
        }
    }

    internal delegate int RegistryRead(nint key, string subkey, string value, uint flags, void* data, uint* size);

    private ImageVersion ReadSiloVersion(nint job)
    {
        var software = GetRegistryHandle(job, NativeProviderConstants.WcVRegSoftwareRoot);
        ImageVersion image;
        try
        {
            var system = GetRegistryHandle(job, NativeProviderConstants.WcVRegSystemRoot);
            try
            {
                image = ReadImageVersion(software, system, ReadValue);
            }
            finally
            {
                PInvoke.RegCloseKey(new HKEY(system));
            }
        }
        finally
        {
            PInvoke.RegCloseKey(new HKEY(software));
        }

        return image;
    }

    private ImageVersion ReadHostVersion()
    {
        var software = OpenKey(@"\REGISTRY\MACHINE\SOFTWARE", (uint)REG_SAM_FLAGS.KEY_READ);
        try
        {
            var system = OpenKey(@"\REGISTRY\MACHINE\SYSTEM\CURRENTCONTROLSET", (uint)REG_SAM_FLAGS.KEY_READ);
            try
            {
                return ReadImageVersion(software, system, ReadValue);
            }
            finally
            {
                PInvoke.RegCloseKey(new HKEY(system));
            }
        }
        finally
        {
            PInvoke.RegCloseKey(new HKEY(software));
        }
    }

    private static RegistryQueryEntry QueryDword(char* name, uint* result) => new RegistryQueryEntry
    {
        Flags = Wdk.RTL_QUERY_REGISTRY_DIRECT | Wdk.RTL_QUERY_REGISTRY_TYPECHECK,
        Name = name,
        EntryContext = result,
        DefaultType = (uint)REG_VALUE_TYPE.REG_DWORD << (int)Wdk.RTL_QUERY_REGISTRY_TYPECHECK_SHIFT,
    };

    internal static ImageVersion ReadImageVersion(nint software, nint system, RegistryRead read)
    {
        var build = ParseBuildNumber(ReadRegistryString(software, CurrentVersion, "CurrentBuildNumber", read));
        uint revision = 0;
        uint size = sizeof(uint);
        var status = read(software, CurrentVersion, "UBR", (uint)REG_ROUTINE_FLAGS.RRF_RT_REG_DWORD, &revision, &size);
        if (status != (int)WIN32_ERROR.ERROR_NOT_FOUND)
            SiloException.CheckWin32(status, "Read image UBR");
        else
            revision = 0;

        _ = ReadRegistryString(software, CurrentVersion, "EditionID", read);

        byte kmode;
        size = 1;
        status = read(system, @"Control\Session Manager\SubSystems", "Kmode", (uint)REG_ROUTINE_FLAGS.RRF_RT_REG_SZ, &kmode, &size);
        if (status == 0)
            throw new InvalidDataException("The one-byte Kmode probe returned unexpected LSTATUS 0.");

        if (status is not ((int)WIN32_ERROR.ERROR_FILE_NOT_FOUND or (int)WIN32_ERROR.ERROR_MORE_DATA))
            SiloException.CheckWin32(status, "Read image Kmode");

        return new ImageVersion(build, revision);
    }

    internal static uint ParseBuildNumber(string value)
    {
        var text = value.AsSpan().TrimStart().ToArray();
        var endIndex = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        while (endIndex < text.Length && SiloDescriptionXml.IntegerDigit(text[endIndex]) is < 10 and var digit)
            text[endIndex++] = (char)('0' + digit);

        if (!int.TryParse(text.AsSpan(0, endIndex), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var build))
            SiloException.CheckWin32((int)WIN32_ERROR.ERROR_INVALID_DATA, "Parse image CurrentBuildNumber");

        return unchecked((uint)build);
    }

    private static string ReadRegistryString(nint key, string subkey, string value, RegistryRead read)
    {
        var size = 128U;
        while (true)
        {
            var data = new byte[size];
            fixed (byte* p = data)
            {
                var status = read(key, subkey, value, (uint)REG_ROUTINE_FLAGS.RRF_RT_REG_SZ, p, &size);
                if (status == (int)WIN32_ERROR.ERROR_MORE_DATA)
                    continue;

                SiloException.CheckWin32(status, "Read image " + value);
                return new string((char*)p, 0, checked((int)size) / 2).TrimEnd('\0');
            }
        }
    }

    private static int ReadValue(nint key, string subkey, string value, uint flags, void* data, uint* size)
    {
        fixed (char* s = subkey, v = value)
            return (int)PInvoke.RegGetValue(new HKEY(key), s, v, (REG_ROUTINE_FLAGS)flags, null, data, size);
    }

    internal void InjectHostFiles(nint job, DownlevelConfiguration configuration)
    {
        if (configuration.HostFiles.Count == 0 || SkipHostFileInjection())
            return;

        var host = _platform.GetHostWindowsDirectory();
        host = host.EndsWith('\\') ? host : host + "\\";

        var fixups = new List<string>();
        var catalogs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in configuration.HostFiles)
        {
            fixups.Add(relativePath);
            var catalog = _platform.GetCatalogHint(host + relativePath);
            if (catalog is not null && catalogs.Add(catalog))
                fixups.Add(@"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}\" + catalog);
        }

        var volume = configuration.ContainerRoot.EndsWith('\\') ? configuration.ContainerRoot : configuration.ContainerRoot + "\\";
        SiloException.CheckHResult(_platform.AttachHostFileFilter(volume), "BfAttachFilter");

        foreach (var relativePath in fixups)
        {
            var destination = volume + "Windows\\" + relativePath;
            if (!HostFileMappingExists(destination, host + relativePath))
                continue;

            var hr = _platform.BindHostFile(job, destination, host + relativePath);
            if (hr != SiloException.FromWin32((int)WIN32_ERROR.ERROR_FILE_NOT_FOUND) && hr != SiloException.FromWin32((int)WIN32_ERROR.ERROR_PATH_NOT_FOUND))
                SiloException.CheckHResult(hr, "Bind downlevel image file " + destination);
        }
    }

    internal partial class PlatformProvider
    {
        internal virtual string GetHostWindowsDirectory()
        {
            const int capacity = (int)PInvoke.MAX_PATH;

            var windows = stackalloc char[capacity];
            var length = PInvoke.GetWindowsDirectory(windows, capacity);
            if (length == 0)
                SiloException.CheckWin32(GetLastErrorOrDefault(), "GetWindowsDirectoryW");

            if (length >= capacity)
                SiloException.CheckWin32((int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "GetWindowsDirectoryW");

            return new string(windows, 0, (int)length);
        }

        internal virtual int AttachHostFileFilter(string volume) => NativeMethods.BfAttachFilter(volume, null);

        internal virtual int BindHostFile(nint job, string destination, string source) =>
            NativeMethods.BfSetupFilter(job, NativeProviderConstants.BindFltFlagReadOnlyMapping, destination, source, null, 0);

        internal virtual bool FindFixupFile(string path, FIND_FIRST_EX_FLAGS flags)
        {
            fixed (char* p = path)
            {
                WIN32_FIND_DATAW data;
                var search = PInvoke.FindFirstFileEx(p, FINDEX_INFO_LEVELS.FindExInfoBasic, &data, FINDEX_SEARCH_OPS.FindExSearchNameMatch, null, flags);
                if ((nint)search.Value == -1)
                    return false;

                PInvoke.FindClose(search);
                return true;
            }
        }

        internal virtual string? GetCatalogHint(string path)
        {
            try
            {
                return ReadCatalogHint(path);
            }
            catch (SiloException error) when (error.NtStatus == NTSTATUS.STATUS_NOT_FOUND.Value ||
                                              error.NtStatus == NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND.Value ||
                                              error.NtStatus == NTSTATUS.STATUS_OBJECT_PATH_NOT_FOUND.Value)
            {
                return null;
            }
        }

        private string? ReadCatalogHint(string path)
        {
            SiloException.CheckNt(NativeMethods.RtlDosPathNameToRelativeNtPathName_U_WithStatus(path, out var name, 0, out var relative), "Convert catalog hint file path");
            try
            {
                var useRelative = relative.Name.Length != 0;
                var attributes = useRelative ? new ObjectAttributes(&relative.Name, relative.ContainingDirectory) : new ObjectAttributes(&name);
                nint file = 0;

                try
                {
                    const FILE_ACCESS_RIGHTS access = FILE_ACCESS_RIGHTS.SYNCHRONIZE | FILE_ACCESS_RIGHTS.FILE_READ_EA;
                    const FILE_SHARE_MODE share = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;
                    const NTCREATEFILE_CREATE_OPTIONS options = NTCREATEFILE_CREATE_OPTIONS.FILE_SYNCHRONOUS_IO_NONALERT | NTCREATEFILE_CREATE_OPTIONS.FILE_NON_DIRECTORY_FILE | NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_FOR_BACKUP_INTENT;
                    var status = NativeMethods.NtCreateFile(out file, (uint)access, &attributes, out _, null, (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, (uint)share, (uint)NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN, (uint)options, null, 0);
                    SiloException.CheckNt(status, "Open catalog hint file");

                    Span<byte> query = stackalloc byte[24];
                    query.Clear();
                    ref var entry = ref MemoryMarshal.AsRef<FILE_GET_EA_INFORMATION>(query);
                    entry.EaNameLength = checked((byte)CatalogHintEaName.Length);
                    CatalogHintEaName.CopyTo(query[EaQueryNameOffset..]);

                    Span<byte> ea = stackalloc byte[288];
                    ea.Clear();

                    fixed (byte* q = query, e = ea)
                    {
                        IoStatusBlock io = default;
                        status = NativeMethods.NtQueryEaFile(file, &io, e, (uint)ea.Length, singleEntry: 1, q, (uint)query.Length, null, restart: 1);
                        if (status == NTSTATUS.STATUS_PENDING.Value)
                        {
                            SiloException.CheckNt(NativeMethods.NtWaitForSingleObject(file, alertable: 0, timeout: null), "Wait for catalog hint EA");
                            status = (int)io.Status;
                        }

                        SiloException.CheckNt(status, "Read catalog hint EA");
                        return DecodeCatalogHint(ea);
                    }
                }
                finally
                {
                    if (file != 0 && file != -1)
                        Close(file);
                }
            }
            finally
            {
                NativeMethods.RtlFreeUnicodeString(&name);
                NativeMethods.RtlReleaseRelativeName(&relative);
            }
        }
    }

    private static readonly UTF8Encoding _catalogHintEncoding = new UTF8Encoding(false, true);

    internal static string? DecodeCatalogHint(ReadOnlySpan<byte> ea)
    {
        if (ea.Length < CatalogHintValueOffset + 4)
            return null;

        var header = MemoryMarshal.Read<FILE_FULL_EA_INFORMATION>(ea);
        if (header.EaNameLength != CatalogHintEaName.Length)
            return null;

        var value = ea[CatalogHintValueOffset..];
        var nameLength = (int)BinaryPrimitives.ReadUInt16LittleEndian(value[2..]);
        if (value[0] != 1 || nameLength is < 1 or > 256 || nameLength > header.EaValueLength - 4 || nameLength > value.Length - 4)
            return null;

        try
        {
            return _catalogHintEncoding.GetString(value.Slice(4, nameLength)) + '\0';
        }
        catch (DecoderFallbackException)
        {
            SiloException.CheckNt(NTSTATUS.STATUS_INTERNAL_ERROR.Value, "Decode catalog hint UTF-8");
            throw;
        }
    }
}