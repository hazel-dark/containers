using Windows.Win32.System.JobObjects;

namespace Containers.Windows.Silos;

// Public SDK constants use CsWin32 at their call sites. Private names below are
// corroborated by these declarations; they are not recovered Microsoft source:
// NtCoreLib/NtJobNative.cs, Google Project Zero, Apache-2.0:
// https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools/blob/852b1586b8c4527f8be3f486801e4830220c76d0/NtCoreLib/NtJobNative.cs
// phnt ntpsapi.h / ntsmss.h (System Informer, MIT):
// https://github.com/winsiderss/phnt/tree/53fbbdc5b5d2b08761db1c7b26bfa8c820924356
// SDK winnt.h (JOB_OBJECT_ALL_ACCESS; absent from pinned CsWin32 metadata):
// https://github.com/microsoft/win32metadata/blob/8c9dd770d11e9148acca39991e9847460ea2e396/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h
internal static class NativeJobConstants
{
    // SDK: STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3f.
    internal const uint JobObjectAllAccess = 0x001f003f;

    // NtCoreLib JobObjectInformationClass and phnt's private class declarations.
    // SDK JOBOBJECTINFOCLASS exposes only reserved names for these values.
    internal const int JobObjectContainerId = 30;
    internal const int JobObjectSiloRootDirectory = 37;
    internal const int JobObjectServerSiloBasicInformation = 38;
    internal const int JobObjectServerSiloUserSharedData = 39;
    internal const int JobObjectServerSiloInitialize = 40;
    internal const int JobObjectServerSiloRunningState = 41;
    internal const int JobObjectSiloSystemRoot = 45;
    internal const int JobObjectThreadImpersonationInformation = 47;

    // phnt's inferred private name is JOB_OBJECT_LIMIT_SILO_READY; NtCoreLib
    // calls it Application. The numeric value is confirmed by the Wc listing.
    internal const JOB_OBJECT_LIMIT JobObjectLimitSiloReady = (JOB_OBJECT_LIMIT)0x00400000;

    // Guesses
    internal const JOB_OBJECT_LIMIT WcJobTotalMemoryLimit = (JOB_OBJECT_LIMIT)0x00200000;
    internal const int WcObjectNamespaceAssignmentProcess = -7;
    internal const uint WcDamRegisterSiloControlCode = 0x0022a020;
    internal const uint WcSmRegisterServerSiloApi = 8;
    internal const byte WcAppSiloShutdownState = 2;
    internal const int WcAppSiloShutdownNotStarted = 0xff;

    // phnt SMAPINUMBER.SmStartServerSiloApi
    internal const uint SmStartServerSiloApi = 7;

    // phnt NtCurrentProcess(), used by OpenProcessToken and NtDuplicateObject.
    internal const int NtCurrentProcess = -1;

    // Might be correct might be incorrect
    internal const int ServerSiloBasicInformationSize = SiloPlatform.ServerSiloBasicInformationSize;
    internal const int ServerSiloUserSharedDataSize = 0x270;
    internal const int ContainerIdInformationSize = 0x24;
    internal const int SiloObjectRootInitialBufferSize = 0x34;
    internal const int SiloObjectRootRetryBufferSize = 0x1000;
    internal const int SingleProcThreadAttributeListSize = 0x30;
    internal const uint StartupInfoExSize = 0x70;
}