using Windows.Win32.Security;
using Windows.Wdk.Foundation;
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

    public void SetupObjects(JobSafeHandle job, ObjectNamespaceConfiguration configuration)
    {
        var flags = configuration.ShadowRoot ? SiloRootShadowRoot : 0u;
        var global = configuration.Directories.FirstOrDefault(d => d.Name.Equals("Global??", StringComparison.OrdinalIgnoreCase));
        if (global is not null)
            flags |= global.Shadow == DirectoryShadow.None ? SiloRootInitializeRoot : SiloRootInitializeRoot | SiloRootShadowGlobal;

        var rootInfo = new SiloObjectRootDirectory();
        rootInfo.ControlFlags = flags;
        SetJob(job, NativeJobConstants.JobObjectSiloRootDirectory, rootInfo.AsBytes());

        var root = OpenDirectory(GetObjectRoot(job), DirectoryAllAccess, required: true, attributes: OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE);
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
                    var unicode = NativeBuffer.UnicodeString(name, configuration.Name.Length);
                    var attributes = NativeBuffer.ObjectAttributes(&unicode, parent, OBJECT_ATTRIBUTE_FLAGS.OBJ_PERMANENT | OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE | OBJECT_ATTRIBUTE_FLAGS.OBJ_OPENIF, descriptor);
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
            var name = NativeBuffer.UnicodeString(n, configuration.Name.Length);
            var destination = NativeBuffer.UnicodeString(t, target.Length);
            var attributes = NativeBuffer.ObjectAttributes(&name, parent, OBJECT_ATTRIBUTE_FLAGS.OBJ_PERMANENT | OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE, descriptor);
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

    private nint OpenDirectory(string path, uint access, bool required, OBJECT_ATTRIBUTE_FLAGS attributes)
    {
        fixed (char* p = path)
        {
            var name = NativeBuffer.UnicodeString(p, path.Length);
            var objectAttributes = NativeBuffer.ObjectAttributes(&name, attributes: attributes);
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
        var directory = OpenDirectory(path, ReadSecurityAccess | Wdk.DIRECTORY_QUERY, required, OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE);
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
            var error = new InvalidDataException($"The security size query unexpectedly returned NTSTATUS 0x{status:x8}.");
            error.Data["NtStatus"] = status;
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
            var name = NativeBuffer.UnicodeString(p, path.Length);
            var attributes = NativeBuffer.ObjectAttributes(&name);
            var openStatus = _platform.OpenObjectLink(out var link, Wdk.SYMBOLIC_LINK_QUERY, &attributes);
            try
            {
                SiloException.CheckNt(openStatus, "NtOpenSymbolicLinkObject");
                Span<char> initial = stackalloc char[(int)InitialObjectLinkTargetBytes / sizeof(char)];
                var size = InitialObjectLinkTargetBytes;
                while (true)
                {
                    using var buffer = new NativeScratchBuffer<char>(initial, (int)size / sizeof(char));
                    buffer.Span.Clear();
                    fixed (char* b = buffer.Span)
                    {
                        var target = new UNICODE_STRING();
                        target.Buffer = b;
                        target.MaximumLength = (ushort)size;
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
        internal virtual int OpenDirectory(out nint handle, uint access, OBJECT_ATTRIBUTES* attributes)
        {
            fixed (nint* output = &handle)
                return Wdk.NtOpenDirectoryObject((HANDLE*)output, access, attributes);
        }

        internal virtual int CreateDirectory(out nint handle, uint access, OBJECT_ATTRIBUTES* attributes, nint shadow, uint flags)
            => NativeMethods.NtCreateDirectoryObjectEx(out handle, access, attributes, shadow, flags);

        internal virtual int OpenObjectLink(out nint handle, uint access, OBJECT_ATTRIBUTES* attributes)
        {
            fixed (nint* output = &handle)
                return Wdk.NtOpenSymbolicLinkObject((HANDLE*)output, access, attributes);
        }

        internal virtual int QueryObjectLink(nint handle, UNICODE_STRING* target, uint* length)
            => Wdk.NtQuerySymbolicLinkObject((HANDLE)handle, target, length);

        internal virtual int CreateObjectLink(nint* handle, uint access, OBJECT_ATTRIBUTES* attributes, UNICODE_STRING* target)
            => NativeMethods.NtCreateSymbolicLinkObject(handle, access, attributes, target);

        internal virtual int SetObjectLink(nint handle, int informationClass, void* data, uint size)
            => NativeMethods.NtSetInformationSymbolicLink(handle, informationClass, data, size);

        internal virtual int QuerySecurity(nint handle, uint information, void* data, uint size, out uint needed)
        {
            fixed (uint* returned = &needed)
                return Wdk.NtQuerySecurityObject((HANDLE)handle, information, new PSECURITY_DESCRIPTOR(data), size, returned);
        }
    }
}