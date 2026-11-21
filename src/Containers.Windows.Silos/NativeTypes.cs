using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Wdk.Foundation;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;

namespace Containers.Windows.Silos;

internal static unsafe class NativeBuffer
{
    internal static UNICODE_STRING UnicodeString(char* buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, ushort.MaxValue / sizeof(char) - 1);
        var s = new UNICODE_STRING
        {
            Length = checked((ushort)(length * sizeof(char))),
            MaximumLength = checked((ushort)((length + 1) * sizeof(char))),
            Buffer = buffer,
        };
        return s;
    }

    internal static OBJECT_ATTRIBUTES ObjectAttributes(UNICODE_STRING* name, nint root = 0, OBJECT_ATTRIBUTE_FLAGS attributes = OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE, void* security = null)
    {
        var objectAttributes = new OBJECT_ATTRIBUTES
        {
            Length = (uint)sizeof(OBJECT_ATTRIBUTES),
            ObjectName = name,
            RootDirectory = new HANDLE(root),
            Attributes = attributes,
            SecurityDescriptor = (SECURITY_DESCRIPTOR*)security,
        };
        return objectAttributes;
    }

    internal static Span<byte> AsBytes<T>(this ref T value) where T : unmanaged =>
        MemoryMarshal.AsBytes(new Span<T>(ref value));

    internal static NativeScratchBuffer<byte> Strings<T>(Span<byte> stackBuffer, scoped in T header, params ReadOnlySpan<string> strings)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        foreach (var value in strings)
            size = checked(size + value.Length * sizeof(char));

        var result = new NativeScratchBuffer<byte>(stackBuffer, size);
        result.Span.Clear();

        var data = result.Span;
        MemoryMarshal.Write(data, in header);

        var offset = Unsafe.SizeOf<T>();
        foreach (var value in strings)
            offset += WriteString(data.Slice(offset), value);

        return result;
    }

    internal static int WriteString(Span<byte> data, string value)
    {
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(data);
        return checked(value.Length * 2);
    }
}

internal readonly ref struct NativeScratchBuffer<T> where T : unmanaged
{
    private readonly T[]? _rented;
    internal Span<T> Span { get; }

    internal NativeScratchBuffer(Span<T> stackBuffer, int length)
    {
        _rented = length > stackBuffer.Length ? ArrayPool<T>.Shared.Rent(length) : null;
        Span = _rented is null ? stackBuffer[..length] : _rented.AsSpan(0, length);
    }

    public void Dispose()
    {
        if (_rented is not null)
        {
            Span.Clear();
            ArrayPool<T>.Shared.Return(_rented);
        }
    }
}

// phnt ntpsapi.h, JOBOBJECT_EXTENDED_LIMIT_INFORMATION_V2. The x64 Wc call
// appends a SIZE_T to the SDK structure for a total length of 0x98.
[StructLayout(LayoutKind.Sequential)]
internal struct ExtendedLimitInformationV2
{
    internal JOBOBJECT_EXTENDED_LIMIT_INFORMATION Limits;
    internal nuint JobTotalMemoryLimit;
}

// WDK mountmgr.h, MOUNTMGR_CREATE_POINT_INPUT (absent from the pinned metadata).
[StructLayout(LayoutKind.Sequential)]
internal struct MountMgrCreatePointInput
{
    internal ushort SymbolicLinkNameOffset;
    internal ushort SymbolicLinkNameLength;
    internal ushort DeviceNameOffset;
    internal ushort DeviceNameLength;
}

// phnt ntpsapi.h, SILOOBJECT_ROOT_DIRECTORY. Set uses ControlFlags and query returns Path.
[StructLayout(LayoutKind.Explicit)]
internal struct SiloObjectRootDirectory
{
    [FieldOffset(0)] internal uint ControlFlags;
    [FieldOffset(0)] internal UNICODE_STRING Path;
}

// NtCoreLib ServerSiloInitInformation / phnt SERVERSILO_INIT_INFORMATION;
[StructLayout(LayoutKind.Sequential)]
internal struct ServerSiloInitialize
{
    internal nint DeleteEvent;
    internal byte Downlevel;
}

[StructLayout(LayoutKind.Explicit, Size = 0x148)]
internal struct SmMessage
{
    // phnt SMAPIMSG; pinned Wc x64 buffer size 0x148 (RtlSendMsgToSm).
    // PORT_MESSAGE (40), ApiNumber (4), ReturnValue (4), union at 48.
    [FieldOffset(0x28)] internal uint ApiNumber;
    [FieldOffset(0x2c)] internal int ReturnValue;
    [FieldOffset(0x30)] internal nint Silo;
    [FieldOffset(0x38)] internal byte Standalone;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WciLayerDescriptor
{
    internal Guid Id;
    internal uint Flags;
    internal char* Path;
}

[StructLayout(LayoutKind.Explicit, Size = 0x220)]
internal unsafe struct CompartmentEntry
{
    [FieldOffset(0)] internal Guid Guid;
    [FieldOffset(0x10)] internal uint Id;
    [FieldOffset(0x14)] internal fixed char Description[257];
    [FieldOffset(0x218)] internal byte Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RegistryQueryEntry
{
    internal nint Routine;
    internal uint Flags;
    internal char* Name;
    internal uint* EntryContext;
    internal uint DefaultType;
    internal nint DefaultData;
    internal uint DefaultLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RelativeNtName
{
    internal UNICODE_STRING Name;
    internal nint ContainingDirectory;
    internal nint CurrentDirectoryReference;
}