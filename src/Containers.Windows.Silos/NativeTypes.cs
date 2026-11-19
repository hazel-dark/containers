using System.Buffers;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Containers.Windows.Silos;

internal static class NativeBuffer
{
    internal static Span<byte> AsBytes<T>(this ref T value) where T : unmanaged =>
        MemoryMarshal.AsBytes(new Span<T>(ref value));
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
    [FieldOffset(0)] internal UnicodeString Path;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UnicodeString
{
    private const int MaximumCharacterLength = (ushort.MaxValue / sizeof(char)) - 1;

    internal ushort Length, MaximumLength;
    internal char* Buffer;

    internal UnicodeString(char* buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumCharacterLength);

        Length = checked((ushort)(length * sizeof(char)));
        MaximumLength = checked((ushort)(Length + sizeof(char)));
        Buffer = buffer;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ObjectAttributes
{
    internal uint Length;
    internal nint RootDirectory;
    internal UnicodeString* ObjectName;
    internal uint Attributes;
    internal nint SecurityDescriptor, SecurityQualityOfService;

    internal ObjectAttributes(UnicodeString* name, nint root = 0, uint attributes = (uint)OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE, nint security = 0)
    {
        Length = (uint)sizeof(ObjectAttributes);
        RootDirectory = root;
        ObjectName = name;
        Attributes = attributes;
        SecurityDescriptor = security;
        SecurityQualityOfService = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    internal nint Status;
    internal nuint Information;
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
    internal UnicodeString Name;
    internal nint ContainingDirectory;
    internal nint CurrentDirectoryReference;
}