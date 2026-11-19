using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Containers.Windows.Silos;

// x64 VRegDriver requests from phnt ntregapi.h, part of System Informer.
// Copyright (c) 2022 Winsider Seminars & Solutions, Inc. (MIT).
// https://github.com/winsiderss/phnt/blob/53fbbdc5b5d2b08761db1c7b26bfa8c820924356/ntregapi.h#L516-L598
// Headers omit the trailing Strings or Keys array. Their sizes are the offsets
// of those arrays in the native requests, including when there are no elements.
[StructLayout(LayoutKind.Sequential)]
internal struct VrLoadDifferencingHiveHeader
{
    internal HANDLE Job;
    internal uint NextLayerIsHost;
    internal uint Flags;
    internal uint LoadFlags;
    internal ushort KeyPathLength;
    internal ushort HivePathLength;
    internal ushort NextLayerKeyPathLength;
    internal HANDLE FileAccessToken;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VrCreateNamespaceNodeHeader
{
    internal HANDLE Job;
    internal ushort ContainerPathLength;
    internal ushort HostPathLength;
    internal uint Flags;
    internal uint AccessMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VrModifyFlags
{
    internal HANDLE Job;
    internal uint AddFlags;
    internal uint RemoveFlags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VrCreateMultipleNamespaceNodesHeader
{
    internal HANDLE Job;
    internal uint NumNewKeys;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NamespaceNodeDataHeader
{
    internal uint AccessMask;
    internal ushort ContainerPathLength;
    internal ushort HostPathLength;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VrGetVirtualRoot
{
    internal HANDLE Job;
    internal uint Index;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VrGetVirtualRootResult
{
    internal HANDLE Key;
}
