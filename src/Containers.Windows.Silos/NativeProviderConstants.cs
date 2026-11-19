using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.Security;

namespace Containers.Windows.Silos;

// Public SDK/WDK values use CsWin32 at call sites where metadata includes them.
// Private selector/flag names are corroborated by Google Project Zero NtCoreLib
// (Apache-2.0) NtJobNative.cs / NtSymbolicLinkNative.cs at:
// https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools/tree/852b1586b8c4527f8be3f486801e4830220c76d0/NtCoreLib
// Directory access and mount-manager CTL_CODE definitions: phnt (MIT), ntobapi.h
// and ntioapi.h at https://github.com/winsiderss/phnt/tree/53fbbdc5b5d2b08761db1c7b26bfa8c820924356
internal static class NativeProviderConstants
{
    internal const uint SiloRootShadowRoot = 1;
    internal const uint SiloRootInitializeRoot = 2;
    internal const uint SiloRootShadowGlobal = 4;
    internal const int SymbolicLinkGlobalInformation = 1;
    internal const int SymbolicLinkAccessMask = 2;

    // DIRECTORY_ALL_ACCESS: STANDARD_RIGHTS_REQUIRED | 0xf; not in pinned metadata.
    internal const uint DirectoryAllAccess = 0x000f000f;
    internal const uint ReadSecurityAccess = PInvoke.ACCESS_SYSTEM_SECURITY | (uint)FILE_ACCESS_RIGHTS.READ_CONTROL;
    internal const uint OwnerGroupDaclSaclSecurityInformation = (uint)(OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION | OBJECT_SECURITY_INFORMATION.GROUP_SECURITY_INFORMATION | OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION | OBJECT_SECURITY_INFORMATION.SACL_SECURITY_INFORMATION);

    // Preserve this mask; SYMBOLIC_LINK_ALL_ACCESS is a different value.
    internal const uint WcObjectLinkCreateAccess = 0x000fffff;
    internal const uint WcSymbolicLinkDropSilo = 1;

    // WDK mountmgr.h: CTL_CODE('m', function, METHOD_BUFFERED, access).
    internal const uint IoctlMountMgrSiloArrival = 0x006dc054;
    internal const uint IoctlMountMgrVolumeArrivalNotification = 0x006d402c;
    internal const uint IoctlMountMgrCreatePoint = 0x006dc000;

    // This is not an NT current-process pseudo handle despite the same bits.
    internal const int WcMountManagerTemplateHandle = -1;

    internal const uint WcVRegRegisterJob = 0x00220004;
    internal const uint WcVRegLoadHive = 0x00220008;
    internal const uint WcVRegAddRedirection = 0x0022000c;
    internal const uint WcVRegModifyFlags = 0x00220010;
    internal const uint WcVRegAddRuntimeVirtualKeys = 0x00220014;
    internal const uint WcVRegDetachJob = 0x00220018;
    internal const uint WcVRegGetRegistryHandle = 0x0022001c;

    internal const uint WcVRegPreviousIsExistingHive = 1;
    internal const uint WcVRegInheritTrust = 1;
    internal const uint WcVRegWriteThrough = 2;
    internal const uint WcVRegTrustedHive = 4;
    internal const uint WcVRegTrustedRedirection = 2;
    internal const uint WcVRegExitNodeRedirection = 4;
    internal const uint WcVRegComRoot = 0;
    internal const uint WcVRegSoftwareRoot = 1;
    internal const uint WcVRegSystemRoot = 2;
    internal const uint WcVRegDisableFlush = 1;

    // BINDFLT_FLAG_READ_ONLY_MAPPING and BINDFLT_FLAG_USE_CURRENT_SILO_MAPPING:
    // https://github.com/microsoft/hcsshim/blob/900183417d29765de2947de22a6e72c22c195550/internal/winapi/bindflt.go
    internal const uint BindFltFlagReadOnlyMapping = 0x00000001;
    internal const uint BindFltFlagUseCurrentSiloMapping = 0x00000004;

    // Do not reinterpret the batch flag word as ordinary BfSetupFilter flags.
    internal const uint WcBindBatchRegisterExceptions = 0x20000000;
    internal const uint WcBindBatchInstallMappings = 0x000000d1;
    internal const uint WcLayerFlags = 0;
    internal const uint WcMaximumIsolationMode = 1;
    internal const uint WcMaximumNestingMode = 1;
    internal const byte WcCompartmentCreationFlag = 1;
    internal const int CompartmentDescriptionCapacity = 257;
    internal const int VolumeGuidPathCapacity = 50;
    internal const int VolumeGuidPathLength = 49;
    internal const uint InitialObjectLinkTargetBytes = 0x104;
}