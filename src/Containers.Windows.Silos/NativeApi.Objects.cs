using Windows.Win32.Foundation;
using Containers.Windows.Silos.DescriptionModel;
using static Containers.Windows.Silos.NativeProviderConstants;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    private static readonly int BufferTooSmall = NTSTATUS.STATUS_BUFFER_TOO_SMALL.Value;
    private static readonly int ObjectNameCollision = NTSTATUS.STATUS_OBJECT_NAME_COLLISION.Value;

    private static bool ObjectMissing(int status) => status == NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND.Value || status == NTSTATUS.STATUS_OBJECT_PATH_NOT_FOUND.Value;

    public void SetupObjects(nint job, ObjectNamespaceConfiguration configuration)
    {
        var flags = configuration.ShadowRoot ? SiloRootShadowRoot : 0u;
        var global = configuration.Directories.FirstOrDefault(d => d.Name.Equals("Global??", StringComparison.OrdinalIgnoreCase));
        if (global is not null)
            flags |= global.Shadow == DirectoryShadow.None ? SiloRootInitializeRoot : SiloRootInitializeRoot | SiloRootShadowGlobal;

        var rootInfo = new SiloObjectRootDirectory { ControlFlags = flags };
        SetJob(job, NativeJobConstants.JobObjectSiloRootDirectory, rootInfo.AsBytes());

        var root = OpenDirectory(GetObjectRoot(job), DirectoryAllAccess, required: true, attributes: (uint)OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE);
        try
        {
            foreach (var directory in configuration.Directories)
                SetupDirectory(root, "", directory);
            foreach (var link in configuration.Symlinks)
                SetupObjectLink(root, link);
        }
        finally
        {
            Close(root);
        }
    }

    private void SetupDirectory(nint parent, string parentPath, ObjectDirectory configuration)
    {
        var hostPath = parentPath + "\\" + configuration.Name;
        var security = configuration.SecurityDescriptorSource is { Length: > 0 } source
            ? ReadDirectorySecurity(source, configuration.Shadow != DirectoryShadow.IfExists)
            : null;
        nint shadow = 0;
        if (configuration.Shadow != DirectoryShadow.None)
        {
            shadow = OpenDirectory(hostPath, Wdk.DIRECTORY_QUERY | Wdk.DIRECTORY_TRAVERSE, configuration.Shadow == DirectoryShadow.Required, 0);
            if (shadow == 0)
                return;
        }

        nint directory = 0;
        try
        {
            try
            {
                fixed (char* name = configuration.Name)
                fixed (byte* descriptor = security)
                {
                    var unicode = new UnicodeString(name, configuration.Name.Length);
                    var attributes = new ObjectAttributes(&unicode, parent, (uint)(OBJECT_ATTRIBUTE_FLAGS.OBJ_PERMANENT | OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE | OBJECT_ATTRIBUTE_FLAGS.OBJ_OPENIF), (nint)descriptor);
                    SiloException.CheckNt(_platform.CreateDirectory(out directory, DirectoryAllAccess, &attributes, shadow, 0), "NtCreateDirectoryObjectEx");
                }
            }
            finally
            {
                Close(shadow);
            }

            foreach (var child in configuration.Directories)
                SetupDirectory(directory, hostPath, child);

            foreach (var link in configuration.Symlinks)
                SetupObjectLink(directory, link);
        }
        finally
        {
            Close(directory);
        }
    }


    private void SetupObjectLink(nint parent, ObjectSymlink configuration)
    {
        if (string.IsNullOrEmpty(configuration.Target) == string.IsNullOrEmpty(configuration.TargetSource))
            SiloException.CheckHResult(HRESULT.E_INVALIDARG.Value, "Specify one object symlink target");

        string target;
        if (configuration.TargetSource is { Length: > 0 } source)
        {
            try
            {
                target = ReadObjectLink(source);
            }
            catch (Exception error)
            {
                SiloEventSource.Log.ObjectLinkSourceFailed(configuration.Name, source, error.Message, (error as SiloException)?.NtStatus ?? 0, error.HResult);
                return;
            }
        }
        else
        {
            target = configuration.Target!;
        }

        var security = ReadDirectorySecurity(@"\", required: false);
        fixed (char* n = configuration.Name, t = target)
        fixed (byte* descriptor = security)
        {
            var name = new UnicodeString(n, configuration.Name.Length);
            var destination = new UnicodeString(t, target.Length);
            var attributes = new ObjectAttributes(&name, parent, (uint)(OBJECT_ATTRIBUTE_FLAGS.OBJ_PERMANENT | OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE), (nint)descriptor);
            nint link = 0;
            try
            {
                var status = _platform.CreateObjectLink(&link, WcObjectLinkCreateAccess, &attributes, &destination);
                if (status == ObjectNameCollision)
                    return;

                SiloException.CheckNt(status, "NtCreateSymbolicLinkObject");

                if (configuration.Scope != SymlinkScope.Local)
                {
                    var dropSilo = configuration.Scope == SymlinkScope.GlobalDropSilo ? WcSymbolicLinkDropSilo : 0u;
                    SiloException.CheckNt(_platform.SetObjectLink(link, SymbolicLinkGlobalInformation, &dropSilo, sizeof(uint)), "SetSymbolicLinkGlobal");
                }

                var mask = configuration.AccessMask;
                if (mask != 0)
                    SiloException.CheckNt(_platform.SetObjectLink(link, SymbolicLinkAccessMask, &mask, sizeof(uint)), "SetSymbolicLinkAccessMask");
            }
            finally
            {
                Close(link);
            }
        }
    }

    private nint OpenDirectory(string path, uint access, bool required, uint attributes)
    {
        fixed (char* p = path)
        {
            var name = new UnicodeString(p, path.Length);
            var objectAttributes = new ObjectAttributes(&name, attributes: attributes);
            var status = _platform.OpenDirectory(out var directory, access, &objectAttributes);
            if (status < 0)
                Close(directory);

            if (!required && ObjectMissing(status))
                return 0;

            SiloException.CheckNt(status, "NtOpenDirectoryObject(" + path + ")");
            return directory;
        }
    }

    private byte[]? ReadDirectorySecurity(string path, bool required)
    {
        var directory = OpenDirectory(path, ReadSecurityAccess | Wdk.DIRECTORY_QUERY, required, (uint)OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE);
        if (directory == 0)
            return null;

        try
        {
            return ReadSecurity(directory);
        }
        finally
        {
            Close(directory);
        }
    }

    private byte[] ReadSecurity(nint handle)
    {
        var status = _platform.QuerySecurity(handle, OwnerGroupDaclSaclSecurityInformation, null, 0, out var length);
        if (status != BufferTooSmall)
        {
            SiloException.CheckNt(status, "QuerySecurityObjectSize");
            SiloEventSource.Log.UnexpectedSecuritySizeStatus(status);
            var error = new InvalidDataException($"The security size query unexpectedly returned NTSTATUS 0x{status:x8}.")
            {
                Data =
                {
                    ["NtStatus"] = status,
                },
            };
            throw error;
        }

        var descriptor = new byte[length];
        fixed (byte* p = descriptor)
            SiloException.CheckNt(_platform.QuerySecurity(handle, OwnerGroupDaclSaclSecurityInformation, p, length, out _), "NtQuerySecurityObject");

        return descriptor;
    }

    private string ReadObjectLink(string path)
    {
        fixed (char* p = path)
        {
            var name = new UnicodeString(p, path.Length);
            var attributes = new ObjectAttributes(&name);
            var openStatus = _platform.OpenObjectLink(out var link, Wdk.SYMBOLIC_LINK_QUERY, &attributes);
            try
            {
                SiloException.CheckNt(openStatus, "NtOpenSymbolicLinkObject");
                var size = InitialObjectLinkTargetBytes;
                while (true)
                {
                    var buffer = new char[size / 2];
                    fixed (char* b = buffer)
                    {
                        var target = new UnicodeString { Buffer = b, MaximumLength = (ushort)size };
                        var required = size;
                        var status = _platform.QueryObjectLink(link, &target, &required);
                        if (status == BufferTooSmall)
                        {
                            if (required <= size || required > ushort.MaxValue || (required & 1) != 0)
                                SiloException.CheckNt(status, $"NtQuerySymbolicLinkObject returned an invalid required size ({required})");

                            size = required;
                            continue;
                        }

                        SiloException.CheckNt(status, "NtQuerySymbolicLinkObject");
                        if (target.Length > size || (target.Length & 1) != 0)
                            throw new InvalidDataException("NtQuerySymbolicLinkObject returned an invalid target length.");

                        return new string(b, 0, target.Length / 2);
                    }
                }
            }
            finally
            {
                Close(link);
            }
        }
    }

    internal partial class PlatformProvider
    {
        internal virtual int OpenDirectory(out nint handle, uint access, ObjectAttributes* attributes)
            => NativeMethods.NtOpenDirectoryObject(out handle, access, attributes);

        internal virtual int CreateDirectory(out nint handle, uint access, ObjectAttributes* attributes, nint shadow, uint flags)
            => NativeMethods.NtCreateDirectoryObjectEx(out handle, access, attributes, shadow, flags);

        internal virtual int OpenObjectLink(out nint handle, uint access, ObjectAttributes* attributes)
            => NativeMethods.NtOpenSymbolicLinkObject(out handle, access, attributes);

        internal virtual int QueryObjectLink(nint handle, UnicodeString* target, uint* length)
            => NativeMethods.NtQuerySymbolicLinkObject(handle, target, length);

        internal virtual int CreateObjectLink(nint* handle, uint access, ObjectAttributes* attributes, UnicodeString* target)
            => NativeMethods.NtCreateProviderSymbolicLink(handle, access, attributes, target);

        internal virtual int SetObjectLink(nint handle, int informationClass, void* data, uint size)
            => NativeMethods.NtSetInformationSymbolicLink(handle, informationClass, data, size);

        internal virtual int QuerySecurity(nint handle, uint information, void* data, uint size, out uint needed)
            => NativeMethods.NtQuerySecurityObject(handle, information, data, size, out needed);
    }
}