using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Wdk.Storage.FileSystem;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

internal static class Pack
{
    internal static int WriteString(byte[] data, int offset, string value)
    {
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(data.AsSpan(offset));
        return checked(value.Length * 2);
    }

    internal static byte[] Strings(int header, params string[] strings)
    {
        var b = new byte[checked(header + strings.Sum(x => x.Length) * 2)];
        foreach (var value in strings)
            header += WriteString(b, header, value);
        return b;
    }

    internal static byte[] Strings<T>(in T header, params string[] strings) where T : unmanaged
    {
        var data = Strings(Unsafe.SizeOf<T>(), strings);
        MemoryMarshal.Write(data, in header);
        return data;
    }

    internal static byte[] PipeSymlink(string name, string target)
    {
        var nameOffset = Unsafe.SizeOf<FILE_PIPE_CREATE_SYMLINK_INPUT>();
        var header = new FILE_PIPE_CREATE_SYMLINK_INPUT
        {
            NameOffset = checked((ushort)nameOffset),
            NameLength = checked((ushort)(name.Length * sizeof(char))),
            SubstituteNameOffset = checked((ushort)(nameOffset + name.Length * sizeof(char))),
            SubstituteNameLength = checked((ushort)(target.Length * sizeof(char))),
            Flags = Wdk.FILE_PIPE_SYMLINK_FLAG_GLOBAL,
        };
        return Strings(in header, name, target);
    }

    // mountmgr.h MOUNTMGR_TARGET_NAME starts with a USHORT byte count, followed by WCHARs.
    // Wc includes two terminating WCHARs outside the counted name.
    internal static byte[] MountTarget(string deviceName)
    {
        var length = checked((ushort)(deviceName.Length * sizeof(char)));
        return Strings(in length, deviceName, "\0\0");
    }

    internal static byte[] MountPoint(string linkName, string deviceName)
    {
        var header = new MountMgrCreatePointInput
        {
            SymbolicLinkNameOffset = checked((ushort)Unsafe.SizeOf<MountMgrCreatePointInput>()),
            SymbolicLinkNameLength = checked((ushort)(linkName.Length * sizeof(char))),
            DeviceNameLength = checked((ushort)(deviceName.Length * sizeof(char))),
        };
        header.DeviceNameOffset = checked((ushort)(header.SymbolicLinkNameOffset + header.SymbolicLinkNameLength));
        return Strings(in header, linkName, deviceName);
    }
}
