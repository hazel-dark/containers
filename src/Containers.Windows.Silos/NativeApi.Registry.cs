using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Storage.FileSystem;
using Windows.Wdk.System.Registry;
using Windows.Wdk.System.SystemServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Registry;
using Containers.Windows.Silos.DescriptionModel;
using static Containers.Windows.Silos.NativeProviderConstants;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    private static readonly int RegistryIntegerOverflow = NTSTATUS.STATUS_INTEGER_OVERFLOW.Value;

    public void SetupRegistry(nint job, RegistryConfiguration configuration)
    {
        using var driver = _platform.OpenRegistryDriver();
        var stacks = new Dictionary<string, string>(StringComparer.Ordinal);

        Ioctl(driver, WcVRegRegisterJob, job.AsBytes());
        try
        {
            foreach (var stack in configuration.HiveStacks)
                stacks[NormalizeHiveStackName(stack.Name)] = SetupHiveStack(driver, job, stack);

            foreach (var redirect in configuration.Redirections)
            {
                var target = redirect.Target ?? "";
                if (!string.IsNullOrEmpty(redirect.HiveStack) && !stacks.TryGetValue(NormalizeHiveStackName(redirect.HiveStack), out target!))
                    SiloException.CheckHResult(HRESULT.E_INVALIDARG.Value, "Resolve registry hive stack " + redirect.HiveStack);

                Ioctl(driver, WcVRegAddRedirection, PackRegistryRedirection(job, redirect, target));
            }

            foreach (var link in configuration.Symlinks)
                SetupRegistryLink(link);
        }
        catch
        {
            CleanupRegistry(job);
            throw;
        }
    }

    // The pinned map lowercases each WCHAR with UCRT, then compares code units.
    private string NormalizeHiveStackName(string name) => string.Create(name.Length, (name, platform: _platform), static (chars, state) =>
    {
        for (var index = 0; index < chars.Length; index++)
            chars[index] = state.platform.LowerCharacter(state.name[index]);
    });

    private string SetupHiveStack(SafeFileHandle driver, nint job, RegistryHiveStack stack)
    {
        var path = "";
        uint previousIsExistingHive = 0;
        foreach (var hive in stack.Hives)
        {
            if (!string.IsNullOrEmpty(hive.ExistingPath))
            {
                previousIsExistingHive = WcVRegPreviousIsExistingHive;
                path = hive.ExistingPath;
                continue;
            }

            var mountedPath = FormatRegistryRoot(hive.MountId, stack.Name);
            Ioctl(driver, WcVRegLoadHive, PackHive(job, hive, mountedPath, path, previousIsExistingHive));
            previousIsExistingHive = 0;
            path = mountedPath;
        }

        var root = OpenKey(path, (uint)REG_SAM_FLAGS.KEY_READ);
        try
        {
            foreach (var key in stack.Keys)
                SetupRegistryKey(root, key);

            foreach (var delete in stack.DeleteKeys)
                DeleteRegistryTree(path + "\\" + delete);
        }
        finally
        {
            Close(root);
        }

        return path;
    }

    internal static byte[] PackHive(nint job, RegistryHive hive, string mountedPath, string previous, uint previousIsExistingHive)
    {
        var header = new VrLoadDifferencingHiveHeader
        {
            Job = new HANDLE(job),
            NextLayerIsHost = previousIsExistingHive,
            Flags = (hive.InheritTrust ? WcVRegInheritTrust : 0) | (hive.TrustedHive ? WcVRegTrustedHive : 0) | (hive.WriteThrough ? WcVRegWriteThrough : 0),
            LoadFlags = PInvoke.REG_HIVE_NO_RM | (hive.ReadOnly ? (uint)PInvoke.REG_OPEN_READ_ONLY : 0) | (hive.Immutable ? (uint)PInvoke.REG_IMMUTABLE : 0),
            KeyPathLength = checked((ushort)(mountedPath.Length * sizeof(char))),
            HivePathLength = checked((ushort)(hive.FilePath.Length * sizeof(char))),
            NextLayerKeyPathLength = checked((ushort)(previous.Length * sizeof(char))),
            FileAccessToken = new HANDLE(unchecked((nint)hive.FileAccessToken)),
        };
        return Pack.Strings(in header, mountedPath, hive.FilePath, previous);
    }

    internal static byte[] PackRegistryRedirection(nint job, RegistryRedirection redirect, string target)
    {
        var header = new VrCreateNamespaceNodeHeader
        {
            Job = new HANDLE(job),
            ContainerPathLength = RegistryStringLength(redirect.Path),
            HostPathLength = RegistryStringLength(target),
            Flags = (redirect.Trusted ? WcVRegTrustedRedirection : 0) | (redirect.ExitNode ? WcVRegExitNodeRedirection : 0),
            AccessMask = redirect.AccessMask,
        };
        return Pack.Strings(in header, redirect.Path, target);
    }

    public void CleanupRegistry(nint job)
    {
        try
        {
            using var driver = _platform.OpenRegistryDriver();
            Ioctl(driver, WcVRegDetachJob, job.AsBytes());
        }
        catch (Exception error)
        {
            var native = error as SiloException;
            SiloEventSource.Log.RegistryCleanupFailed(native?.Operation ?? error.Message, native?.NtStatus ?? 0, native?.Win32Error ?? 0, error.HResult);
        }
    }

    public nint GetRegistryHandle(nint job, uint type)
    {
        using var driver = _platform.OpenRegistryDriver();
        return GetRegistryHandle(driver, job, type);
    }

    private nint GetRegistryHandle(SafeFileHandle driver, nint job, uint type)
    {
        var request = new VrGetVirtualRoot { Job = new HANDLE(job), Index = type };
        VrGetVirtualRootResult output = default;
        Ioctl(driver, WcVRegGetRegistryHandle, request.AsBytes(), output.AsBytes());
        return output.Key;
    }

    public void ModifyRegistryFlags(nint job, uint add, uint remove)
    {
        using var driver = _platform.OpenRegistryDriver();
        var request = new VrModifyFlags { Job = new HANDLE(job), AddFlags = add, RemoveFlags = remove };
        Ioctl(driver, WcVRegModifyFlags, request.AsBytes());
    }

    public void AddRuntimeVirtualKeys(nint job, IReadOnlyList<RuntimeVirtualKey> keys)
    {
        using var driver = _platform.OpenRegistryDriver();
        Ioctl(driver, WcVRegAddRuntimeVirtualKeys, PackRuntimeVirtualKeys(job, keys));
    }

    internal static byte[] PackRuntimeVirtualKeys(nint job, IReadOnlyList<RuntimeVirtualKey> keys)
    {
        long size = sizeof(VrCreateMultipleNamespaceNodesHeader);
        foreach (var key in keys)
        {
            size += sizeof(NamespaceNodeDataHeader) + RegistryStringLength(key.Path) + RegistryStringLength(key.Target);
            if (size > Array.MaxLength)
                SiloException.CheckNt(RegistryIntegerOverflow, "Size runtime virtual key IOCTL");
        }

        var data = new byte[(int)size];
        var header = new VrCreateMultipleNamespaceNodesHeader { Job = new HANDLE(job), NumNewKeys = (uint)keys.Count };
        MemoryMarshal.Write(data, in header);

        var offset = sizeof(VrCreateMultipleNamespaceNodesHeader);
        foreach (var key in keys)
        {
            var node = new NamespaceNodeDataHeader
            {
                AccessMask = key.AccessMask,
                ContainerPathLength = checked((ushort)(key.Path.Length * sizeof(char))),
                HostPathLength = checked((ushort)(key.Target.Length * sizeof(char))),
                Flags = 0,
            };

            MemoryMarshal.Write(data.AsSpan(offset), in node);
            offset += sizeof(NamespaceNodeDataHeader);
            offset += Pack.WriteString(data, offset, key.Path);
            offset += Pack.WriteString(data, offset, key.Target);
        }

        return data;
    }

    private static ushort RegistryStringLength(string value)
    {
        if (value.Length > ushort.MaxValue / 2)
            SiloException.CheckNt(RegistryIntegerOverflow, "Encode registry IOCTL string");

        return checked((ushort)(value.Length * sizeof(char)));
    }

    private void SetupRegistryKey(nint parent, RegistryKeyConfiguration configuration)
    {
        var security = default(byte[]);
        if (!string.IsNullOrEmpty(configuration.SecurityDescriptorSource))
        {
            var source = OpenKey(configuration.SecurityDescriptorSource, PInvoke.ACCESS_SYSTEM_SECURITY | (uint)REG_SAM_FLAGS.KEY_READ);
            try
            {
                security = ReadSecurity(source);
            }
            finally
            {
                Close(source);
            }
        }

        fixed (char* p = configuration.Name)
        fixed (byte* sd = security)
        {
            var name = new UnicodeString(p, configuration.Name.Length);
            var attributes = new ObjectAttributes(&name, parent, security: (nint)sd);
            nint key = 0;
            try
            {
                var status = _platform.CreateKey(&key, (uint)REG_SAM_FLAGS.KEY_ALL_ACCESS, &attributes, (uint)(configuration.Volatile ? REG_OPEN_CREATE_OPTIONS.REG_OPTION_VOLATILE : REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE), null);
                if (status != ObjectNameCollision)
                    SiloException.CheckNt(status, "NtCreateKey(" + configuration.Name + ")");

                foreach (var child in configuration.Keys)
                {
                    if (configuration.Volatile && !child.Volatile)
                        SiloException.CheckHResult(HRESULT.E_INVALIDARG.Value, "Create a persistent key below a volatile key");

                    SetupRegistryKey(key, child);
                }

                foreach (var value in configuration.Values)
                {
                    RequireRegistryValuePayload(value);
                    SetRegistryValue(key, value.Name, value.Type, value.Data);
                }
            }
            finally
            {
                Close(key);
            }
        }
    }

    internal static void RequireRegistryValuePayload(RegistryValue value)
    {
        if (!value.HasPayload)
            throw new InvalidOperationException($"Registry value '{value.Name}' requires a payload.");
    }

    private void SetupRegistryLink(RegistrySymlink link)
    {
        fixed (char* p = link.Path)
        {
            var name = new UnicodeString(p, link.Path.Length);
            var attributes = new ObjectAttributes(&name);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                nint key = 0;
                uint disposition = 0;
                try
                {
                    SiloException.CheckNt(
                        _platform.CreateKey(
                            &key,
                            (uint)FILE_ACCESS_RIGHTS.DELETE | (uint)(REG_SAM_FLAGS.KEY_CREATE_LINK | REG_SAM_FLAGS.KEY_SET_VALUE),
                            &attributes,
                            (uint)(REG_OPEN_CREATE_OPTIONS.REG_OPTION_VOLATILE | REG_OPEN_CREATE_OPTIONS.REG_OPTION_CREATE_LINK),
                            &disposition),
                        "Create registry link");

                    if (disposition == (uint)REG_CREATE_KEY_DISPOSITION.REG_CREATED_NEW_KEY)
                    {
                        SetRegistryValue(key, "SymbolicLinkValue", (uint)REG_VALUE_TYPE.REG_LINK, MemoryMarshal.AsBytes(link.Target.AsSpan()));
                        return;
                    }

                    SiloException.CheckNt(_platform.DeleteKey(key), "Replace registry link");
                }
                finally
                {
                    Close(key);
                }
            }
        }

        SiloException.CheckHResult(SiloException.FromWin32((int)WIN32_ERROR.ERROR_INTERNAL_ERROR), "Create registry link");
    }

    private void SetRegistryValue(nint key, string name, uint type, ReadOnlySpan<byte> data)
    {
        fixed (char* p = name)
        fixed (byte* bytes = data)
        {
            var unicode = new UnicodeString(p, name.Length);
            SiloException.CheckNt(_platform.SetValue(key, &unicode, type, bytes, (uint)data.Length), "NtSetValueKey(" + name + ")");
        }
    }

    private nint OpenKey(string path, uint access, nint parent = 0, bool allowMissing = false)
    {
        fixed (char* p = path)
        {
            var name = new UnicodeString(p, path.Length);
            var attributes = new ObjectAttributes(&name, parent);
            var status = _platform.OpenKey(out var key, access, &attributes);
            if (status < 0)
                Close(key);

            if (allowMissing && status == NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND.Value)
                return 0;

            SiloException.CheckNt(status, "NtOpenKey(" + path + ")");
            return key;
        }
    }

    private void DeleteRegistryTree(string path)
    {
        var key = OpenKey(path, (uint)REG_SAM_FLAGS.KEY_ALL_ACCESS, allowMissing: true);
        if (key == 0)
            return;

        try
        {
            KEY_CACHED_INFORMATION keyInfo = default;
            SiloException.CheckNt(_platform.QueryKey(key, (int)KEY_INFORMATION_CLASS.KeyCachedInformation, &keyInfo, (uint)sizeof(KEY_CACHED_INFORMATION), out _), "NtQueryKey(KeyCachedInformation)");

            var bufferSize = keyInfo.MaxNameLen + sizeof(KEY_BASIC_INFORMATION);
            if (bufferSize > Array.MaxLength)
                SiloException.CheckNt(RegistryIntegerOverflow, "Size registry key enumeration buffer");

            var data = new byte[(int)bufferSize];
            for (uint index = 0; index < keyInfo.SubKeys; index++)
            {
                string subkey;
                fixed (byte* bytes = data)
                {
                    var status = _platform.EnumerateKey(key, index, (int)KEY_INFORMATION_CLASS.KeyBasicInformation, bytes, (uint)data.Length, out _);
                    if (status == NTSTATUS.STATUS_NO_MORE_ENTRIES.Value)
                        break;

                    SiloException.CheckNt(status, "NtEnumerateKey");
                    var basic = (KEY_BASIC_INFORMATION*)bytes;
                    var name = (char*)&basic->Name;
                    var nameSize = basic->NameLength;
                    if (nameSize > (bytes + data.Length) - (byte*)name || (nameSize & 1) != 0)
                        throw new InvalidDataException("NtEnumerateKey returned an invalid key name length.");

                    subkey = new string(name, 0, (int)nameSize / sizeof(char));
                }

                DeleteRegistryTree(path + @"\" + subkey);
            }

            SiloException.CheckNt(_platform.DeleteKey(key), "NtDeleteKey(" + path + ")");
        }
        finally
        {
            Close(key);
        }
    }

    internal partial class PlatformProvider
    {
        internal virtual char LowerCharacter(char value) => char.ToLowerInvariant(value);

        internal virtual SafeFileHandle OpenRegistryDriver()
        {
            const string path = @"\Device\VRegDriver";
            var driver = new SafeFileHandle(0, ownsHandle: true);
            fixed (char* p = path)
            {
                var name = new UnicodeString(p, path.Length);
                var attributes = new ObjectAttributes(&name, attributes: 0);
                var status = NativeMethods.NtCreateFile(
                    out var handle,
                    (uint)(FILE_ACCESS_RIGHTS.SYNCHRONIZE | FILE_ACCESS_RIGHTS.FILE_READ_DATA | FILE_ACCESS_RIGHTS.FILE_WRITE_DATA),
                    &attributes,
                    out _,
                    null,
                    0,
                    0,
                    (uint)NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN_IF,
                    (uint)NTCREATEFILE_CREATE_OPTIONS.FILE_SYNCHRONOUS_IO_ALERT,
                    null,
                    0);

                Marshal.InitHandle(driver, handle);
                if (status < 0)
                {
                    driver.Dispose();
                    SiloException.CheckNt(status, "Open VRegDriver");
                }
            }

            return driver;
        }

        internal virtual int OpenKey(out nint key, uint access, ObjectAttributes* attributes)
            => NativeMethods.NtOpenKey(out key, access, attributes);

        internal virtual int CreateKey(nint* key, uint access, ObjectAttributes* attributes, uint options, uint* disposition)
            => NativeMethods.NtCreateProviderKey(key, access, attributes, 0, null, options, disposition);

        internal virtual int DeleteKey(nint key) => NativeMethods.NtDeleteKey(key);

        internal virtual int QueryKey(nint key, int informationClass, void* data, uint length, out uint needed)
            => NativeMethods.NtQueryKey(key, informationClass, data, length, out needed);

        internal virtual int EnumerateKey(nint key, uint index, int informationClass, void* data, uint length, out uint needed)
            => NativeMethods.NtEnumerateKey(key, index, informationClass, data, length, out needed);

        internal virtual int SetValue(nint key, UnicodeString* name, uint type, void* data, uint length)
            => NativeMethods.NtSetValueKey(key, name, 0, type, data, length);
    }
}
