using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Containers.Windows.Silos;

public static unsafe partial class WcifsWorkspace
{
    private static SafeFileHandle OpenWorkspaceEntry(string path,
        uint access,
        bool followReparse,
        FILE_SHARE_MODE share = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS;
        if (!followReparse)
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

        fixed (char* name = path)
        {
            var handle = PInvoke.CreateFile(new PCWSTR(name), access, share, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, flags);
            if ((nint)handle == -1)
                throw WorkspaceWin32Error("CreateFileW", path);

            return new SafeFileHandle(handle, ownsHandle: true);
        }
    }

    private static FILE_ATTRIBUTE_TAG_INFO GetAttributes(SafeFileHandle file, string path)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx(new HANDLE(file.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
            throw WorkspaceWin32Error("FileAttributeTagInfo", path);

        return info;
    }

    private static WcifsWorkspaceEntryId DirectoryIdentity(string path, bool followReparse = false, uint? expectedTag = 0)
    {
        using var file = OpenWorkspaceEntry(path, (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES, followReparse);
        var attributes = GetAttributes(file, path);
        if ((attributes.FileAttributes & (uint)FileAttributes.Directory) == 0 || (expectedTag is uint tag && attributes.ReparseTag != tag))
            throw new InvalidDataException($"The WCIFS workspace directory '{path}' has unexpected attributes.");

        FILE_ID_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx(new HANDLE(file.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, &info, (uint)sizeof(FILE_ID_INFO)))
            throw WorkspaceWin32Error("FileIdInfo", path);

        // FILE_ID_128 is opaque. Use a Guid to round-trip its 16 bytes.
        return new WcifsWorkspaceEntryId(info.VolumeSerialNumber, new Guid(new ReadOnlySpan<byte>(&info.FileId, 16)));
    }

    private static FileStream OpenManifest(string path)
    {
        var file = OpenWorkspaceEntry(path, (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ, followReparse: false, FILE_SHARE_MODE.FILE_SHARE_READ);
        try
        {
            if ((GetAttributes(file, path).FileAttributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"The WCIFS workspace manifest '{path}' must be an ordinary file.");

            return new FileStream(file, FileAccess.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static Win32Exception WorkspaceWin32Error(string operation, string path) => new Win32Exception(Marshal.GetLastPInvokeError(), $"{operation} failed for '{path}'.");

    private static void CreateDirectoryExclusive(string path)
    {
        fixed (char* name = path)
        {
            if (!PInvoke.CreateDirectory(new PCWSTR(name), null))
                throw WorkspaceWin32Error("CreateDirectoryW", path);
        }
    }

    private static string FinalDirectoryPath(string path)
    {
        using var file = OpenWorkspaceEntry(path, (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES, followReparse: true);
        var buffer = new char[260];
        while (true)
        {
            uint length;
            fixed (char* result = buffer)
                length = PInvoke.GetFinalPathNameByHandle(new HANDLE(file.DangerousGetHandle()), result, (uint)buffer.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);

            if (length == 0)
                throw WorkspaceWin32Error("GetFinalPathNameByHandleW", path);

            if (length < (uint)buffer.Length)
                return new string(buffer, 0, (int)length);

            buffer = new char[checked((int)length + 1)];
        }
    }

    private static void CreateLayerJunction(string alias, string target)
    {
        // ntifs.h REPARSE_DATA_BUFFER, MountPointReparseBuffer. Offsets are bytes
        // relative to PathBuffer. Include each name's terminating UTF-16 NUL.
        const int outerHeader = 8;
        const int mountHeader = 8;
        var substitute = @"\??\" + target;
        var substituteBytes = checked(substitute.Length * sizeof(char));
        var printOffset = substituteBytes + sizeof(char);
        var printBytes = checked(target.Length * sizeof(char));
        var size = outerHeader + mountHeader + printOffset + printBytes + sizeof(char);
        if (size > PInvoke.MAXIMUM_REPARSE_DATA_BUFFER_SIZE)
            throw new PathTooLongException($"The layer path '{target}' is too long for a junction.");

        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, PInvoke.IO_REPARSE_TAG_MOUNT_POINT);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(size - outerHeader));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substituteBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)printOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printBytes);
        MemoryMarshal.AsBytes(substitute.AsSpan()).CopyTo(buffer.AsSpan(outerHeader + mountHeader));
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(buffer.AsSpan(outerHeader + mountHeader + printOffset));

        CreateDirectoryExclusive(alias);
        using var file = OpenWorkspaceEntry(alias, (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE, followReparse: false);
        fixed (byte* data = buffer)
        {
            uint returned;
            if (!PInvoke.DeviceIoControl(new HANDLE(file.DangerousGetHandle()), PInvoke.FSCTL_SET_REPARSE_POINT, data, (uint)buffer.Length, null, 0, &returned, null))
                throw WorkspaceWin32Error("Creating a layer junction", alias);
        }
    }
}