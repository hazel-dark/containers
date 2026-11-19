namespace Containers.Windows.Silos;

internal static class SiloPlatform
{
    internal const int ServerSiloBasicInformationSize = 40;
    internal const int MinimumBuild = 26100;
    internal const string SupportedVersion = "windows10.0.26100";

    [System.Runtime.Versioning.SupportedOSPlatform(SupportedVersion)]
    internal static SiloTerminationReason? GetNotificationReason(IJobNotificationApi api, nint job, nuint code, int status)
    {
        try
        {
            return SiloTerminationNotification.Classify(api.IsServer(job), code, status);
        }
        catch (SiloException error)
        {
            SiloEventSource.Log.ModeQueryFailed(error.HResult);
            return null;
        }
    }

    internal static SiloTerminationReason OtherServerTerminationReason(int status) =>
        status == global::Windows.Win32.Foundation.NTSTATUS.STATUS_SYSTEM_PROCESS_TERMINATED
            ? SiloTerminationReason.Terminated
            : SiloTerminationReason.UnexpectedExit;
}

/// <summary>Raw reason values dispatched by the Windows Server 2025 Wc notification handler.</summary>
public enum SiloTerminationReason : uint
{
    NormalExit = 0,
    UnexpectedExit = 1,
    Reboot = 2,
    Terminated = 3,
}